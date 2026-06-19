using FixPortal.Ci.Backend.Api.Dashboard.Model;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// In-memory holder for the latest snapshot served to clients. Reads come from
/// here, so requests never do disk I/O and never race with the background
/// writer's file replacement. The file store is used only for cold-start restore
/// and durability.
///
/// Single-writer by design: only the startup restore and the one
/// <c>DashboardRefreshWorker</c> background service write <c>Current</c>; many
/// request threads read it. The <c>volatile</c> field guarantees safe reference
/// publication for that model. A second concurrent writer would require an
/// <c>Interlocked</c>/lock-based check-then-set instead.
/// </summary>
public sealed class DashboardSnapshotState
{
    private volatile DashboardSnapshot? _current;
    private volatile DashboardSnapshot? _public;
    private volatile string? _lastAuthError;

    public string? LastAuthError => _lastAuthError;

    public void SetAuthError(string? error) => _lastAuthError = error;

    public DashboardSnapshot? Current => _current;

    public DashboardSnapshot? Public => _public;

    public void Update(DashboardSnapshot current, DashboardSnapshot publicSnap)
    {
        _current = current;
        _public = publicSnap;
    }

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

        var anyPublicFailing = publicWorkflows.Any(w => w.State == SignalState.Failure);
        if (!anyPublicFailing)
        {
            return full.CiTrend.Select(b => b.State == CiTrendState.Failing 
                ? new CiTrendBucket(b.BucketStart, CiTrendState.Passing) 
                : b).ToList();
        }

        return full.CiTrend;
    }
}
