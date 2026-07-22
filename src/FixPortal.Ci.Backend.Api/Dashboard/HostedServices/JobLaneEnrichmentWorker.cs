using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Medium-cadence enrichment for one configured <see cref="JobLaneOptions"/> lane
/// (deploys, packages, …): finds jobs whose name matches the lane's patterns inside
/// each repo's workflows and writes them to the lane's cache. Decoupled from the
/// 60s refresh so the extra jobs-API calls don't load the board's rate budget.
/// <see cref="RepoEnrichmentWorker{T}"/> owns the loop; a null collect keeps the
/// prior cached signals. One instance per lane (see Program.cs), each given its
/// lane key and the matching keyed cache.
/// </summary>
public sealed class JobLaneEnrichmentWorker(
    string laneKey,
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<IReadOnlyList<JobSignal>> cache,
    IOptions<DashboardOptions> options,
    ILogger<JobLaneEnrichmentWorker> logger
) : RepoEnrichmentWorker<IReadOnlyList<JobSignal>>(client, inventory, cache, logger)
{
    // Case-insensitive to match GetEffectiveJobLanes' OrdinalIgnoreCase de-dup: a
    // configured key differing only in casing (e.g. "Deploys") must still resolve.
    private JobLaneOptions? Lane =>
        options
            .Value.GetEffectiveJobLanes()
            .FirstOrDefault(l => string.Equals(l.Key, laneKey, StringComparison.OrdinalIgnoreCase));

    protected override bool Enabled => Lane?.Enabled == true;
    protected override TimeSpan Cadence => TimeSpan.FromSeconds(Lane?.RefreshSeconds ?? 300);
    protected override string Name => $"{Lane?.Label ?? laneKey} signals";

    protected override async Task<IReadOnlyList<JobSignal>?> CollectAsync(GitHubRepoDto repo, CancellationToken ct)
    {
        var patterns = Lane?.Patterns ?? [];
        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch;
        var signals = new List<JobSignal>();
        try
        {
            var workflows = await Inventory.GetWorkflowsAsync(repo.Name, ct);
            foreach (var wf in workflows)
            {
                signals.AddRange(await CollectWorkflowJobsAsync(repo, wf, branch, patterns, ct));
            }
        }
        catch (Exception ex)
            when (ex is HttpRequestException or GitHubRateLimitException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to collect {Lane} for {Repo}; keeping last-known-good.", laneKey, repo.Name);
            return null; // keep the prior cached signals
        }
        return signals;
    }

    // Walk the workflow's recent default-branch runs newest-first, surfacing the newest
    // definite state for each distinct deploy target. A gated prod deploy is skipped in
    // most runs (so it ran in an older run than the always-running dev deploy); selecting
    // per-target rather than returning the first run with any signal stops the dev deploy
    // from shadowing prod's last real run. Stops scanning once every seen target has a
    // signal (or the workflow runs no matching job), bounding the extra jobs-API calls.
    private async Task<IReadOnlyList<JobSignal>> CollectWorkflowJobsAsync(
        GitHubRepoDto repo,
        GitHubWorkflowDto wf,
        string branch,
        IReadOnlyList<string> patterns,
        CancellationToken ct
    )
    {
        var collected = new List<RunWithJobs>();
        var page = 1;
        var pageSize = 10;
        var maxRuns = Lane?.MaxRunsToScan ?? 30;

        while (collected.Count < maxRuns)
        {
            var runs = await Client.GetRecentDefaultBranchRunsAsync(repo.Name, wf.Id, branch, pageSize, page, ct);
            if (runs.Count == 0)
            {
                break;
            }

            foreach (var run in runs)
            {
                if (collected.Count >= maxRuns)
                {
                    break;
                }
                var jobs = await Client.GetRunJobsAsync(repo.Name, run.Id, ct);
                collected.Add(new RunWithJobs(run, jobs));
                var result = GitHubOrgClient.SelectLaneSignals(wf.Name, repo.HtmlUrl, collected, patterns);
                if (result.Complete)
                {
                    return result.Signals;
                }
            }

            if (runs.Count < pageSize)
            {
                break;
            }

            page++;
        }
        return GitHubOrgClient.SelectLaneSignals(wf.Name, repo.HtmlUrl, collected, patterns).Signals;
    }
}
