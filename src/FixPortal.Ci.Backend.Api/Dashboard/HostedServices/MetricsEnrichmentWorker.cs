using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Slow-cadence enrichment (default 12h): clones each repo and runs Lizard,
/// writing results to the metrics cache. <see cref="RepoEnrichmentWorker{T}"/>
/// owns the loop; this type supplies cadence, the enabled flag, and the scan.
/// A null scan result keeps the prior cached metrics.
/// </summary>
public sealed class MetricsEnrichmentWorker(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    LizardScanner scanner,
    PerRepoCache<RepoMetrics> cache,
    IOptions<DashboardOptions> options,
    ILogger<MetricsEnrichmentWorker> logger)
    : RepoEnrichmentWorker<RepoMetrics>(client, inventory, cache, logger)
{
    protected override bool Enabled => options.Value.MetricsEnabled;
    protected override TimeSpan Cadence => TimeSpan.FromSeconds(options.Value.MetricsRefreshSeconds);
    protected override string Name => "Repo metrics";

    protected override async Task<RepoMetrics?> CollectAsync(GitHubRepoDto repo, CancellationToken ct)
    {
        var metrics = await scanner.ScanAsync(repo.Name, ct);
        if (metrics is not null)
        {
            logger.LogInformation("Metrics for {Repo}: {Nloc} NLOC, avg CCN {Ccn:0.0}, {Fns} fns.",
                repo.Name, metrics.Nloc, metrics.AvgComplexity, metrics.FunctionCount);
        }

        return metrics;
    }
}
