using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Restores the persisted snapshot into the in-memory holder during host
/// startup, before the server begins accepting traffic. Implemented as
/// <see cref="IHostedLifecycleService"/> so <c>StartingAsync</c> completes ahead
/// of Kestrel — the endpoint never serves 204 while a valid persisted snapshot
/// is still loading.
/// </summary>
public sealed class SnapshotRestoreService(
    IDashboardSnapshotStore store,
    DashboardSnapshotState state,
    ILogger<SnapshotRestoreService> logger
) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        DashboardSnapshot? restored;
        try
        {
            restored = await store.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // host shutdown during restore — do not swallow
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore persisted snapshot; starting with an empty board.");
            return;
        }

        if (restored is null)
        {
            return;
        }

        // Sanitization is deliberately OUTSIDE the load's try: a bug here must fail
        // startup fast, not be swallowed into "empty board" alongside unreadable files.
        var sanitized = restored with
        {
            Repositories =
            [
                .. restored.Repositories.Select(DashboardRefreshService.WithoutHeadScopedReviewState),
            ],
        };
        // Prefer the persisted public trend (accurate, public-only); fall
        // back to the lossy reclassification for pre-PublicCiTrend snapshots.
        state.Update(
            sanitized,
            DashboardSnapshotState.ComputePublicSnapshot(sanitized, sanitized.PublicCiTrend)
        );
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
