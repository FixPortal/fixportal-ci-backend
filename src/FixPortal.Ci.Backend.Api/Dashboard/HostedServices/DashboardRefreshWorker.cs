using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

public sealed class DashboardRefreshWorker(
    DashboardRefreshService refreshService,
    IOptions<DashboardOptions> options,
    ILogger<DashboardRefreshWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cold-start restore happens in SnapshotRestoreService.StartingAsync,
        // before the server accepts traffic.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.RefreshSeconds));
        await RefreshSafelyAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshSafelyAsync(stoppingToken);
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await refreshService.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down; let the loop unwind normally.
            throw;
        }
        catch (Exception ex)
        {
            // A failure here must not crash the collector: keep the last known
            // good snapshot in place and retry on the next tick.
            logger.LogError(ex, "Dashboard refresh failed; keeping last known good snapshot.");
        }
    }
}
