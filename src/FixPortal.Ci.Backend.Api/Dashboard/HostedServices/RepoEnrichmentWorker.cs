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
    ILogger logger) : BackgroundService where T : class
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

        // Stagger/jitter initial sweep: 0 to 15 seconds
        var jitter = Random.Shared.Next(0, 15000);
        try
        {
            await Task.Delay(jitter, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var firstSweepSuccessful = false;
        while (!firstSweepSuccessful && !stoppingToken.IsCancellationRequested)
        {
            var success = await SweepSafelyAsync(stoppingToken);
            if (success)
            {
                // A successful sweep ends cold-start even if it produced no cached
                // values: an org with no matching repos legitimately yields an empty
                // cache, and retrying every 5 minutes forever would never converge.
                // The steady-state cadence picks up data once any repo emits it.
                firstSweepSuccessful = true;
                if (cache.IsEmpty)
                {
                    logger.LogInformation("{Name} cold-start sweep completed but produced no cached values (no matching repos?); switching to steady-state cadence.", Name);
                }
            }
            else
            {
                logger.LogWarning("{Name} cold-start sweep failed; retrying in 5 minutes.", Name);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            using var timer = new PeriodicTimer(Cadence);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepSafelyAsync(stoppingToken);
            }
        }
    }

    // Per-sweep tally used to decide whether a cold-start sweep genuinely succeeded.
    protected readonly record struct SweepOutcome(int Total, int Written, int Failed);

    private async Task<bool> SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            var repos = await Inventory.GetRepositoriesAsync(ct);
            var outcome = await RunSweepAsync(repos, ct);
            // Cold-start succeeds when the org legitimately has nothing to write — no
            // repos, or repos with no matching signal yet (both leave the cache empty
            // by design). It has NOT succeeded when repos existed, nothing was written,
            // and at least one collect threw: that is a transient outage, so report
            // failure and let the 5-minute cold-start retry cover it instead of dropping
            // to the slow steady cadence (e.g. 12h for metrics) with an empty cache.
            // (Collectors that soft-fail by returning null rather than throwing are not
            // counted as failures here — distinguishing those needs a richer collect
            // contract than the null-keeps-prior one, out of scope for this guard.)
            return !(outcome.Total > 0 && outcome.Written == 0 && outcome.Failed > 0);
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
        var written = 0;
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
                    written++;
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
                logger.LogWarning(ex, "Failed to collect enrichment for {Repo} during {Name} sweep; keeping prior cached value.", repo.Name, Name);
            }
        }
        return new SweepOutcome(repos.Count, written, failed);
    }
}
