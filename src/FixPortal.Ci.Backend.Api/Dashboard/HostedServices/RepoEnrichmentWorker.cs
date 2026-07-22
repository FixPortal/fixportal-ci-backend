using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Shared skeleton for the per-repo enrichment workers (metrics, deploys). Each
/// runs on its own cadence, decoupled from the 60s snapshot refresh so the board
/// stays responsive, the snapshot stays single-writer, and the extra API calls
/// don't load the board's rate budget. A sweep lists repos and writes a per-repo
/// value to the cache; a <c>null</c> collect keeps the prior value (per-repo skip
/// or soft-fail). No-ops when the enrichment is disabled.
/// </summary>
public abstract class RepoEnrichmentWorker<T>(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<T> cache,
    ILogger logger
) : BackgroundService
    where T : class
{
    // Exposed to subclasses so they reuse this one instance for their per-repo
    // collect rather than capturing their own copy of the injected client (which
    // would trip CS9107 — value both captured and forwarded to the base ctor).
    protected GitHubOrgClient Client { get; } = client;

    // The shared repo/workflow inventory. The sweep lists repos through it (one
    // org fetch per refresh cycle, shared with the board), and job-lane subclasses
    // read each repo's workflows through it instead of re-listing them.
    protected GitHubInventoryCache Inventory { get; } = inventory;

    protected abstract bool Enabled { get; }
    protected abstract TimeSpan Cadence { get; }
    protected abstract string Name { get; }

    /// <summary>Collect the enrichment for one repo. Return null to keep the prior cached value.</summary>
    protected abstract Task<T?> CollectAsync(GitHubRepoDto repo, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("{Name} disabled; worker idle.", Name);
            return;
        }

        if (!await WaitForInitialJitterAsync(stoppingToken))
        {
            return;
        }

        await RunColdStartAsync(stoppingToken);
        if (!stoppingToken.IsCancellationRequested)
        {
            using var timer = new PeriodicTimer(Cadence);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepSafelyAsync(stoppingToken);
            }
        }
    }

    private static async Task<bool> WaitForInitialJitterAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(0, 15000), stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunColdStartAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await SweepSafelyAsync(stoppingToken))
            {
                // An empty result is legitimate, so a successful sweep always ends cold-start.
                if (cache.IsEmpty)
                {
                    logger.LogInformation(
                        "{Name} cold-start sweep completed but produced no cached values (no matching repos?); switching to steady-state cadence.",
                        Name
                    );
                }

                return;
            }

            logger.LogWarning("{Name} cold-start sweep failed; retrying in 5 minutes.", Name);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Per-sweep tally used to decide whether a cold-start sweep genuinely succeeded.
    protected readonly record struct SweepOutcome(int Total, int Failed);

    private async Task<bool> SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            var repos = await Inventory.GetRepositoriesAsync(ct);
            var outcome = await RunSweepAsync(repos, ct);
            // Cold-start has converged only when repos existed and NONE threw. Any hard
            // per-repo failure (even with some repos written) means a transiently-failing
            // repo may be stranded with no cached value, so report failure and let the
            // 5-minute cold-start retry cover it rather than dropping to the slow steady
            // cadence (e.g. 12h for metrics) with a gap. An org with no repos legitimately
            // converges (Total == 0). Note: collectors that soft-fail by returning null
            // instead of throwing are not counted here — distinguishing a soft-fail null
            // from a legitimate no-data null (e.g. a repo with no merged PR) needs a
            // tri-state collect contract, out of scope for this guard.
            return outcome is not { Total: > 0, Failed: > 0 };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down; let the loop unwind normally.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Name} sweep failed; keeping prior cache.", Name);
            return false;
        }
    }

    // The write loop, separated from repo-listing so it is unit-testable without a
    // live GitHubOrgClient. Writes the collected value for each repo, keeping the
    // prior cached value when CollectAsync returns null. Returns the per-sweep tally.
    protected async Task<SweepOutcome> RunSweepAsync(IReadOnlyList<GitHubRepoDto> repos, CancellationToken ct)
    {
        var failed = 0;
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var value = await CollectAsync(repo, ct);
                if (value is not null)
                {
                    cache.Update(repo.Name, value);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // Any per-repo failure (auth/authz, transport, JSON, or a filesystem/process
            // error from a metrics scan) skips only this repo's enrichment — it must not
            // abort the sweep and leave every other repo's signal stale for this cycle.
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(
                    ex,
                    "Failed to collect enrichment for {Repo} during {Name} sweep; keeping prior cached value.",
                    repo.Name,
                    Name
                );
            }
        }
        return new SweepOutcome(repos.Count, failed);
    }
}
