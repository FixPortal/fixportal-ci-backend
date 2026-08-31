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

    // The merge endpoint calls this the instant GitHub itself reports a pull request as not
    // mergeable (a real conflict or a blocked required check) rather than waiting for the next
    // MergeStateEnrichmentWorker sweep (up to MergeState:RefreshSeconds, 120s by default) to
    // catch up. GitHub's own merge attempt is the most authoritative verdict there is; throwing
    // it away and leaving the pill "Ready to merge" for up to two minutes reads as a broken
    // button. HeadSha is left untouched: this PR's head has not moved, only its mergeability
    // verdict has, and the next sweep still supersedes this patch with a fresh verdict.
    public void MarkNotMergeable(string repo, int pullNumber)
    {
        var snapshots = _snapshots;
        if (snapshots.Current is null)
        {
            return;
        }
        _snapshots = new Snapshots(
            snapshots.Current with
            {
                Repositories = PatchNotMergeable(snapshots.Current.Repositories, repo, pullNumber),
            },
            snapshots.Public is null
                ? null
                : snapshots.Public with
                {
                    Repositories = PatchNotMergeable(snapshots.Public.Repositories, repo, pullNumber),
                }
        );
    }

    private static IReadOnlyList<RepositorySnapshot> PatchNotMergeable(
        IReadOnlyList<RepositorySnapshot> repositories,
        string repo,
        int pullNumber
    ) =>
        repositories
            .Select(r =>
                !string.Equals(r.Name, repo, StringComparison.OrdinalIgnoreCase)
                    ? r
                    : r with
                    {
                        PullRequests = r
                            .PullRequests.Select(pr => pr.Number == pullNumber ? pr with { ReadyToMerge = false } : pr)
                            .ToList(),
                    }
            )
            .ToList();

    public static DashboardSnapshot ComputePublicSnapshot(
        DashboardSnapshot full,
        IReadOnlyList<CiTrendBucket>? publicCiTrend = null
    )
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

    // Cold-start FALLBACK only, for a snapshot persisted before PublicCiTrend
    // existed (SnapshotRestoreService now prefers the persisted public trend when
    // present). The persisted full trend is computed from ALL repos (public AND
    // private) and the snapshot carries no per-repo run history, so a historical
    // Failing bucket cannot be attributed to a public vs a private repo. We must
    // therefore NEVER surface a Failing we cannot prove is public — otherwise a
    // private repo's failure leaks onto the public trend. Reclassify every Failing
    // bucket to NoData; the first live refresh (~one refresh cycle) recomputes the
    // public trend accurately from fresh public-only runs and replaces this.
    private static IReadOnlyList<CiTrendBucket>? BuildPublicCiTrendFromSnapshot(
        DashboardSnapshot full,
        IReadOnlyList<RepositorySnapshot> publicRepos
    )
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

        return full
            .CiTrend.Select(b =>
                b.State == CiTrendState.Failing ? new CiTrendBucket(b.BucketStart, CiTrendState.NoData) : b
            )
            .ToList();
    }
}
