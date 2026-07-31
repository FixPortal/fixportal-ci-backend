using NodaTime;

// Public snapshot members are serialized API-contract fields, including fields not read by this assembly.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace FixPortal.Ci.Backend.Api.Dashboard.Model;

public enum SignalState
{
    Success,
    Failure,
    Running,
    Unknown,
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
    string? WorkflowFile = null
);

public sealed record WorkflowSnapshot(string Name, string File, SignalState State, WorkflowRun? LastRun);

public enum ReviewSignalState
{
    /// <summary>The reviewer demonstrably ran and left nothing outstanding.</summary>
    Clean,

    /// <summary>The reviewer has items still open; Count carries how many.</summary>
    Outstanding,

    /// <summary>Required here, but no evidence it has run. Never render as clean.</summary>
    Pending,

    /// <summary>Not required on this pull request.</summary>
    Disabled,
}

public sealed record ReviewSignal(string Name, ReviewSignalState State, int? Count, string? HtmlUrl);

public sealed record PullRequest(
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    bool IsDraft,
    Instant CreatedAt,
    // Optional and trailing so every existing construction site keeps compiling and
    // an older frontend simply never sees the field. Null means "nothing to show":
    // enrichment has not run, the author is excluded, or no reviewers are configured.
    IReadOnlyList<ReviewSignal>? ReviewSignals = null
);

public sealed record MergedPullRequest(
    int Number,
    string Title,
    string Author,
    string Repo,
    string HtmlUrl,
    Instant MergedAt
);

public sealed record RepoMetrics(
    int Nloc,
    double AvgComplexity,
    int FunctionCount,
    int HighComplexityCount,
    Instant ComputedAt
);

public sealed record JobSignal(string Workflow, string Name, SignalState State, string HtmlUrl, Instant UpdatedAt);

public sealed record RepositorySnapshot(
    string Name,
    string HtmlUrl,
    bool Private,
    IReadOnlyList<WorkflowSnapshot> Workflows,
    IReadOnlyList<PullRequest> PullRequests,
    RepoMetrics? Metrics,
    IReadOnlyList<JobSignal>? Deploys,
    IReadOnlyList<JobSignal>? Packages,
    MergedPullRequest? LastMergedPr = null
);

public sealed record SummaryCount(string Key, int Count);

// One hour of org-wide CI health. NoData means no runs that hour and nothing to
// carry forward (only at the leading edge). Passing means at least one run and
// none failed. Failing means any run failed (worst state across repos).
public enum CiTrendState
{
    NoData,
    Passing,
    Failing,
}

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

// PublicCiTrend is the public-only CI trend, persisted alongside the all-repo
// CiTrend so a cold-start restore can surface the accurate public trend instead
// of the lossy reclassification in DashboardSnapshotState.BuildPublicCiTrendFromSnapshot
// (which cannot attribute a historical Failing bucket to a public vs private
// repo). It is set only on the persisted full snapshot; the public snapshot
// served to clients already carries the public trend in CiTrend, so it leaves
// PublicCiTrend null. Null on a snapshot written before this field existed, in
// which case restore falls back to the reclassification.
public sealed record DashboardSnapshot(
    Instant RefreshedAt,
    string Org,
    IReadOnlyList<RepositorySnapshot> Repositories,
    IReadOnlyList<SummaryCount> Summary,
    MergedPullRequest? LastMergedPr,
    IReadOnlyList<CiTrendBucket>? CiTrend = null,
    IReadOnlyList<CiTrendBucket>? PublicCiTrend = null
);
