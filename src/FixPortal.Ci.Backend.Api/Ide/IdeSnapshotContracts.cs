using NodaTime;

namespace FixPortal.Ci.Backend.Api.Ide;

public sealed record IdeSnapshot(
    int SchemaVersion,
    string SnapshotId,
    Instant ObservedAt,
    string Organization,
    IReadOnlyList<IdeRepository> Repositories
);

public sealed record IdeRepository(string Name, bool Private, IReadOnlyList<IdeWorkflow> Workflows);

public sealed record IdeWorkflow(string File, IReadOnlyList<IdeRun> RecentRuns);

public sealed record IdeRun(
    long RunId,
    int Attempt,
    string HeadSha,
    string? Branch,
    string? Event,
    string? Status,
    string? Conclusion,
    string Url,
    Instant UpdatedAt
);
