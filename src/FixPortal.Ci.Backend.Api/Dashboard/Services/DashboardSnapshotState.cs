using FixPortal.Ci.Backend.Api.Dashboard.Model;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// In-memory holder for the latest snapshot served to clients. Reads come from
/// here, so requests never do disk I/O and never race with the background
/// writer's file replacement. The file store is used only for cold-start restore
/// and durability.
///
/// Single-writer by design: only the startup restore and the one
/// <c>DashboardRefreshWorker</c> background service publish snapshots; many
/// request threads read them. Both snapshots are published together as one
/// immutable <see cref="Snapshots"/> pair behind a single <c>volatile</c>
/// reference, so a reader can never observe a refreshed <c>Current</c> paired
/// with a stale <c>Public</c> (the two fields are swapped atomically by one
/// reference write). A second concurrent writer would require an
/// <c>Interlocked</c>/lock-based check-then-set instead.
/// </summary>
public sealed class DashboardSnapshotState
{
    private sealed record Snapshots(DashboardSnapshot? Current, DashboardSnapshot? Public);

    private volatile Snapshots _snapshots = new(null, null);
    private volatile string? _lastAuthError;

    public string? LastAuthError => _lastAuthError;

    public void SetAuthError(string? error) => _lastAuthError = error;

    public DashboardSnapshot? Current => _snapshots.Current;

    public DashboardSnapshot? Public => _snapshots.Public;

    public void Update(DashboardSnapshot current, DashboardSnapshot publicSnap) =>
        _snapshots = new Snapshots(current, publicSnap);

    public static DashboardSnapshot ComputePublicSnapshot(DashboardSnapshot full, IReadOnlyList<CiTrendBucket>? publicCiTrend = null)
    {
        var publicRepos = full.Repositories.Where(r => !r.Private).ToList();
        var lastMerged = DashboardRefreshService.PickLatestMerged(publicRepos.Select(r => r.LastMergedPr));
        var trend = publicCiTrend ?? BuildPublicCiTrendFromSnapshot(full, publicRepos);

        return new DashboardSnapshot(
            full.RefreshedAt,
            full.Org,
            publicRepos,
            DashboardRefreshService.BuildSummary(publicRepos),
            lastMerged,
            trend
        );
    }

    // Cold-start approximation ONLY (SnapshotRestoreService, before the first live
    // refresh). The persisted full trend is computed from ALL repos (public AND
    // private) and the snapshot carries no per-repo run history, so a historical
    // Failing bucket cannot be attributed to a public vs a private repo. We must
    // therefore NEVER surface a Failing we cannot prove is public — otherwise a
    // private repo's failure leaks onto the public trend. Reclassify every Failing
    // bucket to Passing; the first live refresh (~one refresh cycle) recomputes the
    // public trend accurately from fresh public-only runs and replaces this.
    private static IReadOnlyList<CiTrendBucket>? BuildPublicCiTrendFromSnapshot(
        DashboardSnapshot full,
        IReadOnlyList<RepositorySnapshot> publicRepos)
    {
        if (full.CiTrend is null)
        {
            return null;
        }
        var publicWorkflows = publicRepos.SelectMany(r => r.Workflows).ToList();
        if (publicRepos.Count == 0 || publicWorkflows.Count == 0)
        {
            return full.CiTrend.Select(b => new CiTrendBucket(b.BucketStart, CiTrendState.NoData)).ToList();
        }

        return full.CiTrend.Select(b => b.State == CiTrendState.Failing
            ? new CiTrendBucket(b.BucketStart, CiTrendState.Passing)
            : b).ToList();
    }
}
