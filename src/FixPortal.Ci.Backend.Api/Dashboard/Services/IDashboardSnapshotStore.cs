using FixPortal.Ci.Backend.Api.Dashboard.Model;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

public interface IDashboardSnapshotStore
{
    Task<DashboardSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(DashboardSnapshot snapshot, CancellationToken cancellationToken);
}
