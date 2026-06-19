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
            if (success && !cache.IsEmpty)
            {
                firstSweepSuccessful = true;
            }
            else
            {
                logger.LogWarning("{Name} cold-start sweep failed or cache remains empty; retrying in 5 minutes.", Name);
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

    private async Task<bool> SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            var repos = await Inventory.GetRepositoriesAsync(ct);
            await RunSweepAsync(repos, ct);
            return true;
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
    // prior cached value when CollectAsync returns null.
    protected async Task RunSweepAsync(IReadOnlyList<GitHubRepoDto> repos, CancellationToken ct)
    {
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
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "Failed to collect enrichment for {Repo} during {Name} sweep; keeping prior cached value.", repo.Name, Name);
            }
        }
    }
}
