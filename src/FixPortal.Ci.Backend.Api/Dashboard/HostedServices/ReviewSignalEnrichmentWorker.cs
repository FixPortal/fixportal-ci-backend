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
    private int _sweepRepos;
    private int _sweepFailedPrs;
    private int _sweepSkippedForBudget;
    private GraphQlRateLimit? _lastRateLimit;

    private static readonly IReadOnlyDictionary<int, PrWatermark> EmptyWatermarks = new Dictionary<int, PrWatermark>();
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>> EmptySignals =
        new Dictionary<int, IReadOnlyList<ReviewSignal>>();

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

        // Logged unconditionally, INCLUDING the zero-query case. A quiet sweep spending
        // nothing is now the expected steady state, so if it logged nothing at all then
        // "working perfectly" and "not running" would look identical from the outside —
        // which is the same silence-reads-as-health defect this feature was fixed for.
        // The line is the proof the worker is alive and the design is doing its job.
        // Failures at Warning, on their own line: a sweep issuing queries that are all
        // refused would otherwise read as ordinary activity in the Information line.
        if (_sweepFailedPrs > 0)
        {
            logger.LogWarning(
                "PR review signals could not be read for {Failed} pull request(s) this sweep; they keep their previous "
                    + "watermark and will be retried. A persistent count here means the token cannot read them at all.",
                _sweepFailedPrs
            );
        }

        logger.LogInformation(
            "PR review signals sweep issued {Queries} GraphQL queries ({Failed} refused) costing {Cost} point(s) across {Repos} repo(s); {Remaining} remaining until {ResetAt}.",
            _sweepQueries,
            _sweepFailedPrs,
            _sweepCost,
            _sweepRepos,
            _lastRateLimit?.Remaining,
            _lastRateLimit?.ResetAt
        );

        _sweepFailedPrs = 0;
        _sweepRepos = 0;
        _sweepCost = 0;
        _sweepQueries = 0;
        _sweepSkippedForBudget = 0;
    }

    protected override async Task<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>?> CollectAsync(
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        _sweepRepos++;

        if (IsBelowReserve())
        {
            _sweepSkippedForBudget++;
            return null;
        }

        // Visibility decides the roster, once, here — everything downstream (which
        // endpoints get called, which pills exist, what the ready-to-merge verdict waits
        // on) follows from this one list. GitHub's scanning products are free on public
        // repositories and off on private ones, so a PublicOnly reviewer on a private repo
        // is not "pending", it is absent.
        var reviewers = options.Value.Reviewers.Where(r => !r.PublicOnly || !repo.Private).ToList();
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

            var batch =
                toFetch.Count > 0
                    ? await Client.GetPullRequestReviewFactsAsync(repo.Name, toFetch, ct)
                    : ReviewFactsBatch.Empty;
            var facts = batch.Facts;
            RecordGraphQlCost(batch);

            // Only pay for the alerts call when a configured reviewer actually reads it,
            // AND only when something is being recomputed — an unchanged sweep must cost
            // nothing at all, which is the entire point of this path.
            var (alerts, secretAlerts) = await FetchScanAlertsAsync(repo.Name, reviewers, toFetch.Count > 0, ct);

            // Commit the watermark LAST, once everything this sweep depends on has
            // succeeded. Advancing it earlier is the subtle version of the bug this whole
            // design exists to prevent: GetOpenCodeScanningAlertCountsAsync swallows only
            // GitHubAuthException, so a transient transport failure propagates to the
            // soft-fail handler below, the cache keeps last-known-good — and an
            // already-advanced watermark would make the next diff empty, silently
            // certifying pull requests whose freshly-fetched facts were just discarded.
            // A brand-new pull request is the worst case: it is not in the cache, so
            // ReviewSignalPriority cannot rescue it either, and its pills would never
            // appear until someone touched the pull request again.
            // A pull request GitHub refused keeps its PREVIOUS watermark rather than the
            // one just observed, so the next diff still sees it as dirty and retries it.
            // Advancing it would mean a repo whose token cannot read one pull request
            // silently reports that pull request as up to date forever — a partial version
            // of the same certify-stale bug, and the harder one to notice because every
            // other pull request in the repo still works.
            _watermarks[repo.Name] = batch.Failed.Count == 0 ? current : WithoutFailed(current, previous, batch.Failed);

            if (diff.Evicted.Count > 0)
            {
                logger.LogInformation(
                    "PR review signals dropped for {Repo} #{Evicted}: no longer open.",
                    repo.Name,
                    string.Join(", #", diff.Evicted)
                );
            }

            RecordObservations(repo.Name, facts.Keys, current);
            return Merge(repo.Name, cached, current, facts, alerts, secretAlerts, reviewers);
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
    /// The scanning-alert counts the configured roster actually reads. Both calls are
    /// skipped unless a reviewer of that source survived the visibility filter AND
    /// something is being recomputed this sweep — an unchanged sweep must cost nothing,
    /// which is the entire point of the incremental path.
    /// </summary>
    private async Task<(IReadOnlyDictionary<int, int>? Alerts, int? SecretAlerts)> FetchScanAlertsAsync(
        string repo,
        IReadOnlyList<ReviewerOptions> reviewers,
        bool recomputing,
        CancellationToken ct
    )
    {
        if (!recomputing)
        {
            return (null, null);
        }

        var alerts = reviewers.Any(r => r.Source == ReviewerSource.CodeScanning)
            ? await Client.GetOpenCodeScanningAlertCountsAsync(repo, ct)
            : null;
        var secretAlerts = reviewers.Any(r => r.Source == ReviewerSource.SecretScanning)
            ? await Client.GetOpenSecretScanningAlertCountAsync(repo, ct)
            : null;
        return (alerts, secretAlerts);
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
        int? secretAlerts,
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
            var repoHtmlUrl = $"https://github.com/{gitHub.Value.Owner}/{repo}";
            signals[pr.Number] = ReviewSignalFactory.Build(
                pr,
                reviewers,
                openAlerts,
                secretAlerts,
                $"{repoHtmlUrl}/pull/{pr.Number}",
                repoHtmlUrl
            );
        }

        return signals;
    }

    /// <summary>
    /// The freshly observed watermarks, except that refused pull requests keep whatever
    /// was known before (or nothing at all, if they were never read). Either way the next
    /// diff still reports them dirty, so a refusal is retried rather than certified.
    /// </summary>
    internal static IReadOnlyDictionary<int, PrWatermark> WithoutFailed(
        IReadOnlyDictionary<int, PrWatermark> current,
        IReadOnlyDictionary<int, PrWatermark> previous,
        IReadOnlyList<int> failed
    )
    {
        var merged = new Dictionary<int, PrWatermark>(current);
        foreach (var number in failed)
        {
            if (previous.TryGetValue(number, out var before))
            {
                merged[number] = before;
            }
            else
            {
                _ = merged.Remove(number);
            }
        }
        return merged;
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
    private void RecordGraphQlCost(ReviewFactsBatch batch)
    {
        // Attempts, not successes. The previous version incremented once per successful
        // call, so a sweep being refused on every query logged "0 GraphQL queries" —
        // identical to a quiet sweep that genuinely spent nothing, and it cost real time
        // to spot. Failures are counted separately rather than hidden.
        _sweepQueries += batch.QueriesIssued;
        _sweepFailedPrs += batch.Failed.Count;
        // Cost comes from the batch, which sums every query it issued. Reading it off the
        // last rate-limit observation instead discarded everything the retry path spent —
        // a refused batch re-queried one PR at a time would report only the final query's
        // cost, understating spend in exactly the scenario the retry exists to handle.
        _sweepCost += batch.PointsSpent;
        if (Client.LastGraphQlRateLimit is { } rateLimit)
        {
            // Still read for Remaining/ResetAt, which are point-in-time and only
            // meaningful as the most recent observation.
            _lastRateLimit = rateLimit;
        }
    }
}
