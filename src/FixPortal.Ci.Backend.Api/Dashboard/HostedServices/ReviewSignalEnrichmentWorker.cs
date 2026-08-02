using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Slow-cadence enrichment (default 900s): fetches each repo's open-PR review state in
/// one batched GraphQL query plus one code-scanning call, and caches a per-PR signal
/// list. Off the 20s board loop deliberately — a per-PR fetch on that cadence would
/// exceed the PAT rate budget several times over. Disabled unless reviewers are
/// configured, so the default deployment issues no extra requests at all.
/// </summary>
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
            var facts = await Client.GetPullRequestReviewFactsAsync(repo.Name, ct);
            RecordGraphQlCost();
            if (facts.Count == 0)
            {
                return new Dictionary<int, IReadOnlyList<ReviewSignal>>();
            }

            // Only pay for the alerts call when a configured reviewer actually reads it.
            var needsAlerts = reviewers.Any(r => r.Source == ReviewerSource.CodeScanning);
            var alerts = needsAlerts ? await Client.GetOpenCodeScanningAlertCountsAsync(repo.Name, ct) : null;

            var signals = new Dictionary<int, IReadOnlyList<ReviewSignal>>();
            foreach (var pr in facts.Values)
            {
                // Dependency bots are out of AI code review by policy, so their PRs carry
                // no pills at all rather than a row of disabled ones.
                if (options.Value.ExcludedAuthors.Contains(pr.AuthorLogin, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                // A null alerts dictionary means unreadable and must stay null per PR.
                // A present dictionary with no entry for this PR means zero open alerts.
                var openAlerts = alerts is null ? (int?)null : alerts.GetValueOrDefault(pr.Number);
                signals[pr.Number] = ReviewSignalFactory.Build(
                    pr,
                    reviewers,
                    openAlerts,
                    $"https://github.com/{gitHub.Value.Owner}/{repo.Name}/pull/{pr.Number}"
                );
            }
            return signals;
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
