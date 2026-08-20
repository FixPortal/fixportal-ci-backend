using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Keeps GitHub's own merge verdict fresh for every open pull request.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="ReviewSignalEnrichmentWorker"/> rather than extra
/// fields on its query, for two reasons. First cost: merge state is four scalars with no
/// connection fan-out, so a whole repository's open pull requests cost one GraphQL point,
/// against the review-facts query's thread-and-comment fan-out. Second availability: the
/// review worker is disabled unless reviewers are configured, and a board filter that
/// silently dies with an unrelated feature's configuration is a worse bug than a second
/// worker.
/// </para>
/// <para>
/// One deliberate exception to that independence: this worker gates its own spend on
/// <see cref="ReviewSignalsOptions.ReserveBudgetPoints"/>. The GraphQL points budget is
/// metered per USER rather than per token, so both workers draw on one pool and a reserve
/// that guards only half of it guards nothing — the merge sweep would drain the floor the
/// review worker had stopped to hold, and take a human's <c>gh</c> down with it. The knob
/// is shared because the budget is shared, not because the features are coupled: reviews
/// being switched off does not disable this worker, it only leaves the reserve at its
/// default. If the two ever need different floors, give merge state its own setting rather
/// than widening this one.
/// </para>
/// <para>
/// It also cannot ride the review worker's freshness policy. That policy refetches a pull
/// request slowly once its pills are all terminal — which is precisely the pull request
/// this worker cares most about. Merge state has no terminal value at all: a pull request
/// goes stale when someone else's merge advances the base branch, an event that touches
/// neither its head SHA nor its updated_at. So every open pull request is refetched every
/// sweep, which the cost profile above makes affordable.
/// </para>
/// </remarks>
public sealed class MergeStateEnrichmentWorker(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<IReadOnlyDictionary<int, PrMergeState>> cache,
    IOptions<MergeStateOptions> options,
    IOptions<ReviewSignalsOptions> reviewOptions,
    TimeProvider timeProvider,
    IClock clock,
    ILogger<MergeStateEnrichmentWorker> logger
) : RepoEnrichmentWorker<IReadOnlyDictionary<int, PrMergeState>>(client, inventory, cache, timeProvider, logger)
{
    protected override bool Enabled => options.Value.Enabled;

    protected override TimeSpan Cadence => TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    protected override string Name => "PR merge state";

    protected override async Task<IReadOnlyDictionary<int, PrMergeState>?> CollectAsync(
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        try
        {
            // The open-PR listing is a conditional REST request that answers 304 — and so
            // costs nothing — when a repository is quiet. Reusing it here means a quiet
            // estate spends almost nothing even though the cadence is short.
            var open = await Client.ListOpenPullRequestWatermarksAsync(repo.Name, ct);
            if (open.Count == 0)
            {
                // An empty dictionary, not null: null means "keep what you had", which for
                // a repository whose last pull request just merged would strand a merge
                // state for a pull request that no longer exists.
                return new Dictionary<int, PrMergeState>();
            }

            return await Client.GetPullRequestMergeStatesAsync(
                repo.Name,
                open.Keys.ToList(),
                ct,
                budget =>
                    ReviewSignalEnrichmentWorker.IsBelowReserve(
                        budget,
                        reviewOptions.Value.ReserveBudgetPoints,
                        clock.GetCurrentInstant()
                    )
            );
        }
        // Same soft-fail set as the review-signal worker: degrade to last-known-good rather
        // than letting a transient transport error count as a sweep failure and drive the
        // 5-minute cold-start retry indefinitely.
        catch (Exception ex)
            when (ex is HttpRequestException or GitHubRateLimitException or GitHubAuthException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to fetch merge state for {Repo}; keeping last-known-good.", repo.Name);
            return null;
        }
    }
}
