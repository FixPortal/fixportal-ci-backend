using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Slow-cadence enrichment (default 300s): finds each repo's most-recently merged
/// pull request and writes it to the cache. <see cref="RepoEnrichmentWorker{T}"/>
/// owns the loop; a null collect keeps the prior value (a repo with no merges, or a
/// soft fetch failure, simply isn't written). Off the 60s loop so it doesn't load
/// the board's rate budget. <c>DashboardRefreshService</c> reduces the per-repo
/// values to one org-wide "last merged".
/// </summary>
public sealed class MergedPrEnrichmentWorker(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<MergedPullRequest> cache,
    IOptions<DashboardOptions> options,
    ILogger<MergedPrEnrichmentWorker> logger
) : RepoEnrichmentWorker<MergedPullRequest>(client, inventory, cache, logger)
{
    protected override bool Enabled => options.Value.MergedPrEnabled;
    protected override TimeSpan Cadence => TimeSpan.FromSeconds(options.Value.MergedPrRefreshSeconds);
    protected override string Name => "Last merged PR";

    protected override async Task<MergedPullRequest?> CollectAsync(GitHubRepoDto repo, CancellationToken ct)
    {
        try
        {
            return await Client.GetLastMergedPullRequestAsync(repo.Name, ct);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or GitHubRateLimitException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to fetch last merged PR for {Repo}; keeping last-known-good.", repo.Name);
            return null;
        }
    }
}
