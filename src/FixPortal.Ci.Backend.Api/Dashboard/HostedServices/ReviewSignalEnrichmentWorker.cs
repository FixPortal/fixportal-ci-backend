using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Incremental review-signal enrichment (default 150s). Each sweep asks the REST open-PR
/// listing what changed — a conditional request that answers 304, and therefore costs
/// nothing, when a repository is quiet — and spends GraphQL points only on the pull
/// requests whose watermark actually moved. Disabled unless reviewers are configured, so
/// the default deployment issues no extra requests at all.
/// </summary>
/// <remarks>
/// <para>
/// It previously swept every repository on a timer, asking for 25 pull-request slots each
/// and paying full connection fan-out for every empty slot. Measured in production that
/// was 1,537 GraphQL points per sweep across 29 repositories — to track 2 open pull
/// requests — against a 5,000-points/hour budget, so it exhausted the hour in nine
/// minutes and spent the remaining fifty-one rate-limited. Cost scaled with the size of
/// the estate; value scales with the number of open pull requests. Nothing connected the
/// two, which is why every attempt to fix it by retuning the cadence only moved the
/// point at which it broke.
/// </para>
/// <para>
/// Three things cover the gaps a naive watermark would leave:
/// <see cref="ReviewSignalPriority"/> refetches non-terminal pills on a short timer
/// (a resolved thread and a completing check both change a pill without moving
/// <c>updated_at</c>) and every pull request on a slow one; eviction drops signals for
/// pull requests that have closed; and the reserve floor still bounds total spend, though
/// it should now never fire.
/// </para>
/// </remarks>
public sealed class ReviewSignalEnrichmentWorker(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>> cache,
    IOptions<ReviewSignalsOptions> options,
    IOptions<GitHubOptions> gitHub,
    TimeProvider timeProvider,
    // Injected rather than read off the base class's TimeProvider: capturing that
    // parameter here as well as forwarding it to the base ctor trips CS9107, the same
    // reason Client is re-exposed as a property. NodaTime for the comparison because
    // ResetAt is an instant in time, not an opaque string.
    IClock clock,
    ILogger<ReviewSignalEnrichmentWorker> logger
)
    : RepoEnrichmentWorker<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(
        client,
        inventory,
        cache,
        timeProvider,
        logger
    )
{
    // No reviewers configured means the feature is off: the base class logs once and
    // the worker idles without issuing a single request.
    protected override bool Enabled => options.Value.Enabled && options.Value.Reviewers.Count > 0;

    protected override TimeSpan Cadence => TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    protected override string Name => "PR review signals";

    // Whole-sweep GraphQL cost, reset each sweep. The design costed this feature against
    // the 5,000/hour REST budget, but the batched query is billed against GraphQL's own
    // 5,000-POINTS/hour budget, priced by connection fan-out — pullRequests(50) x
    // reviewThreads(100) x comments(1) is not one point. Nothing else measures that, and
    // exhausting it degrades silently (rate-limit errors become last-known-good, then a
    // TTL expiry to no pills), so the number is logged rather than guessed at.
    private int _sweepCost;
    private int _sweepQueries;
    private int _sweepSkippedForBudget;
    private GraphQlRateLimit? _lastRateLimit;

    private static readonly IReadOnlyDictionary<int, PrWatermark> EmptyWatermarks = new Dictionary<int, PrWatermark>();
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>> EmptySignals =
        new Dictionary<int, IReadOnlyList<ReviewSignal>>();
    private static readonly IReadOnlyDictionary<int, PrReviewFacts> EmptyFacts = new Dictionary<int, PrReviewFacts>();

    // Sweep-to-sweep state. Sweeps are sequential (RepoEnrichmentWorker.RunSweepAsync
    // awaits each repo in turn) and this worker is a singleton, so plain dictionaries are
    // safe. In-memory deliberately: a restart re-fetches every open PR once, which now
    // costs a handful of points rather than a full estate sweep.
    private readonly Dictionary<string, IReadOnlyDictionary<int, PrWatermark>> _watermarks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, Dictionary<int, Instant>> _observedAt = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnSweepCompleted()
    {
        // Warning, not Information: a sweep that stopped early left repos on a stale or
        // absent signal, and the whole failure mode this guard exists for is that the
        // board degrades quietly enough that nobody looks. Silence must not read as health.
        if (_sweepSkippedForBudget > 0)
        {
            logger.LogWarning(
                "PR review signals sweep skipped {Skipped} repo(s) to hold the {Reserve}-point GraphQL reserve; "
                    + "{Remaining} remaining until {ResetAt}. Those repos keep their previous signal until it ages out.",
                _sweepSkippedForBudget,
                options.Value.ReserveBudgetPoints,
                _lastRateLimit?.Remaining,
                _lastRateLimit?.ResetAt
            );
        }

        if (_sweepQueries > 0)
        {
            logger.LogInformation(
                "PR review signals sweep issued {Queries} GraphQL queries costing {Cost} point(s); {Remaining} remaining until {ResetAt}.",
                _sweepQueries,
                _sweepCost,
                _lastRateLimit?.Remaining,
                _lastRateLimit?.ResetAt
            );
        }

        _sweepCost = 0;
        _sweepQueries = 0;
        _sweepSkippedForBudget = 0;
    }

    protected override async Task<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>?> CollectAsync(
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        if (IsBelowReserve())
        {
            _sweepSkippedForBudget++;
            return null;
        }

        var reviewers = options.Value.Reviewers;
        try
        {
            // Discovery, on the REST budget and usually free: an unchanged repository
            // answers 304 from the shared ETag store, which GitHub does not charge.
            var current = await Client.ListOpenPullRequestWatermarksAsync(repo.Name, ct);
            var previous = _watermarks.GetValueOrDefault(repo.Name) ?? EmptyWatermarks;
            var diff = PrWatermarkDiff.Compute(previous, current);

            var cached = Cache.TryGet(repo.Name, out var prior) && prior is not null ? prior : EmptySignals;
            var observed = _observedAt.TryGetValue(repo.Name, out var seen) ? seen : [];

            // Transitions the watermark cannot see (a resolved thread, a check completing
            // on an unchanged head) get a slow forced refetch instead of never updating.
            var due = ReviewSignalPriority
                .SelectDue(cached, observed, ReviewSignalPriority.RefreshInterval, clock.GetCurrentInstant())
                .Where(current.ContainsKey);

            var toFetch = diff.Dirty.Union(due).ToList();

            var facts =
                toFetch.Count > 0 ? await Client.GetPullRequestReviewFactsAsync(repo.Name, toFetch, ct) : EmptyFacts;
            if (toFetch.Count > 0)
            {
                RecordGraphQlCost();
            }

            _watermarks[repo.Name] = current;

            // Only pay for the alerts call when a configured reviewer actually reads it,
            // AND only when something is being recomputed — an unchanged sweep must cost
            // nothing at all, which is the entire point of this path.
            var needsAlerts = toFetch.Count > 0 && reviewers.Any(r => r.Source == ReviewerSource.CodeScanning);
            var alerts = needsAlerts ? await Client.GetOpenCodeScanningAlertCountsAsync(repo.Name, ct) : null;

            RecordObservations(repo.Name, facts.Keys, current);
            return Merge(repo.Name, cached, current, facts, alerts, reviewers);
        }
        // GitHubAuthException belongs with the soft-fail transports: a PAT missing the
        // GraphQL scope must degrade to last-known-good and let cold start converge,
        // matching the code-scanning path in GetOpenCodeScanningAlertCountsAsync. Letting
        // it escape counts as a per-repo sweep failure and cold start retries every
        // 5 minutes indefinitely — worse than a stale pill, and unfixable-looking.
        catch (Exception ex)
            when (ex is HttpRequestException or GitHubRateLimitException or GitHubAuthException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to fetch review signals for {Repo}; keeping last-known-good.", repo.Name);
            return null;
        }
    }

    /// <summary>
    /// True when the last observed budget is below the configured reserve and that
    /// observation is still current. Closed loop: every query already returns
    /// rateLimit { remaining resetAt }, so the worker can see it is running out — before
    /// this guard it read that number, logged it, and swept again anyway.
    /// </summary>
    /// <summary>
    /// Stamps when each refetched pull request was observed, and forgets ones that are no
    /// longer open. The stamps drive <see cref="ReviewSignalPriority"/>, so letting them
    /// accumulate for closed pull requests would slowly turn the priority queue into a
    /// leak.
    /// </summary>
    private void RecordObservations(
        string repo,
        IEnumerable<int> fetched,
        IReadOnlyDictionary<int, PrWatermark> stillOpen
    )
    {
        if (!_observedAt.TryGetValue(repo, out var observed))
        {
            observed = [];
            _observedAt[repo] = observed;
        }

        foreach (var closed in observed.Keys.Where(n => !stillOpen.ContainsKey(n)).ToList())
        {
            _ = observed.Remove(closed);
        }

        var now = clock.GetCurrentInstant();
        foreach (var number in fetched)
        {
            observed[number] = now;
        }
    }

    /// <summary>
    /// Last-known-good for everything still open, overwritten by whatever was refetched.
    /// The merge is what makes the incremental path safe: an untouched pull request keeps
    /// the signal it already had rather than being recomputed or dropped, while one that
    /// closed disappears — fixing a pill that used to outlive its pull request.
    /// </summary>
    private IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>> Merge(
        string repo,
        IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>> cached,
        IReadOnlyDictionary<int, PrWatermark> stillOpen,
        IReadOnlyDictionary<int, PrReviewFacts> facts,
        IReadOnlyDictionary<int, int>? alerts,
        IReadOnlyList<ReviewerOptions> reviewers
    )
    {
        var signals = new Dictionary<int, IReadOnlyList<ReviewSignal>>();
        foreach (var (number, existing) in cached.Where(entry => stillOpen.ContainsKey(entry.Key)))
        {
            signals[number] = existing;
        }

        foreach (var pr in facts.Values)
        {
            // Dependency bots are out of AI code review by policy, so their PRs carry no
            // pills at all rather than a row of disabled ones. Remove rather than skip:
            // merging onto a prior value means a PR cached before the exclusion was
            // configured would otherwise keep its pills forever.
            if (options.Value.ExcludedAuthors.Contains(pr.AuthorLogin, StringComparer.OrdinalIgnoreCase))
            {
                _ = signals.Remove(pr.Number);
                continue;
            }

            // A null alerts dictionary means unreadable and must stay null per PR. A
            // present dictionary with no entry for this PR means zero open alerts.
            var openAlerts = alerts is null ? (int?)null : alerts.GetValueOrDefault(pr.Number);
            signals[pr.Number] = ReviewSignalFactory.Build(
                pr,
                reviewers,
                openAlerts,
                $"https://github.com/{gitHub.Value.Owner}/{repo}/pull/{pr.Number}"
            );
        }

        return signals;
    }

    private bool IsBelowReserve() =>
        IsBelowReserve(_lastRateLimit, options.Value.ReserveBudgetPoints, clock.GetCurrentInstant());

    /// <summary>
    /// The reserve decision, as a pure function of the last observed budget, the
    /// configured floor and the current instant. internal so it can be tested directly:
    /// the alternative is driving a BackgroundService round a fake clock to assert one
    /// boolean, and the case that matters most here (not latching past ResetAt) is the
    /// one that hides best behind that much machinery.
    /// </summary>
    internal static bool IsBelowReserve(GraphQlRateLimit? rateLimit, int reserve, Instant now)
    {
        if (reserve <= 0 || rateLimit is not { } budget || budget.Remaining >= reserve)
        {
            return false;
        }

        // Load-bearing: once the guard trips, this worker stops querying, so Remaining is
        // frozen at the value that tripped it and can never improve on its own. Without
        // expiring the observation at ResetAt the guard would latch until the process
        // restarted — trading a starved CLI for a permanently blank board, which is worse
        // than the bug. An unparseable ResetAt fails OPEN for the same reason: a guard
        // that cannot prove the budget is still low must not be the thing that wedges it.
        var resetAt = InstantPattern.ExtendedIso.Parse(budget.ResetAt ?? string.Empty);
        return resetAt.Success && now < resetAt.Value;
    }

    // Sweeps are sequential (RepoEnrichmentWorker.RunSweepAsync awaits each repo in
    // turn), so plain accumulation is safe here.
    private void RecordGraphQlCost()
    {
        _sweepQueries++;
        if (Client.LastGraphQlRateLimit is { } rateLimit)
        {
            _lastRateLimit = rateLimit;
            _sweepCost += rateLimit.Cost;
        }
    }
}
