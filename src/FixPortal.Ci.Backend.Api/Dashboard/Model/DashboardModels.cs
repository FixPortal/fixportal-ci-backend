using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Model;

public enum SignalState
{
    Success,
    Failure,
    Running,
    Unknown
}

// One workflow run, normalized from GitHub. Conclusion is null while running.
public sealed record WorkflowRun(
    string? Status,
    string? Conclusion,
    string HtmlUrl,
    string Title,
    int RunNumber,
    string? Branch,
    string? Event,
    Instant UpdatedAt,
    string? Repository = null,
    string? WorkflowFile = null);

public sealed record WorkflowSnapshot(
    string Name,
    string File,
    SignalState State,
    WorkflowRun? LastRun);

public sealed record PullRequest(
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    bool IsDraft,
    Instant CreatedAt);

public sealed record MergedPullRequest(
    int Number,
    string Title,
    string Author,
    string Repo,
    string HtmlUrl,
    Instant MergedAt);

public sealed record RepoMetrics(
    int Nloc,
    double AvgComplexity,
    int FunctionCount,
    int HighComplexityCount,
    Instant ComputedAt);

public sealed record JobSignal(
    string Workflow,
    string Name,
    SignalState State,
    string HtmlUrl,
    Instant UpdatedAt);

public sealed record RepositorySnapshot(
    string Name,
    string HtmlUrl,
    bool Private,
    IReadOnlyList<WorkflowSnapshot> Workflows,
    IReadOnlyList<PullRequest> PullRequests,
    RepoMetrics? Metrics,
    IReadOnlyList<JobSignal>? Deploys,
    IReadOnlyList<JobSignal>? Packages,
    MergedPullRequest? LastMergedPr = null);

public sealed record SummaryCount(string Key, int Count);

// One hour of org-wide CI health. NoData means no runs that hour and nothing to
// carry forward (only at the leading edge). Passing means at least one run and
// none failed. Failing means any run failed (worst state across repos).
public enum CiTrendState { NoData, Passing, Failing }

public sealed record CiTrendBucket(Instant BucketStart, CiTrendState State)
{
    /// <summary>
    /// True when this bucket's state was carried over from a previous snapshot during a
    /// degraded refresh (MergeTrends) rather than observed from fresh CI data. A backfilled
    /// bucket is never re-copied by a subsequent backfill, so a once-real state cannot chain
    /// forward indefinitely across repeated degraded refreshes.
    /// </summary>
    public bool IsBackfilled { get; init; }
}

public sealed record DashboardSnapshot(
    Instant RefreshedAt,
    string Org,
    IReadOnlyList<RepositorySnapshot> Repositories,
    IReadOnlyList<SummaryCount> Summary,
    MergedPullRequest? LastMergedPr,
    IReadOnlyList<CiTrendBucket>? CiTrend = null);
