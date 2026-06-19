namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

public sealed class DashboardOptions
{
    public required string SnapshotPath { get; init; }
    public required int RefreshSeconds { get; init; }
    public bool ExcludeArchived { get; init; } = true;
    public bool IncludeReusable { get; init; }
    public bool IncludeCodeQl { get; init; } = true;
    public int RunHistoryPageSize { get; init; } = 30;
    public bool MetricsEnabled { get; init; } = true;
    public int MetricsRefreshSeconds { get; init; } = 43_200; // 12h
    public string MetricsWorkDirectory { get; init; } = "";    // "" -> temp subdir
    public bool MergedPrEnabled { get; init; } = true;
    public int MergedPrRefreshSeconds { get; init; } = 300;
    public IReadOnlyList<JobLaneOptions> JobLanes { get; init; } =
    [
        new JobLaneOptions { Key = "deploys", Label = "Deploys", Patterns = ["deploy"] },
        new JobLaneOptions
        {
            Key = "packages", Label = "Packages",
            Patterns = ["publish", "package", "docker", "image", "release", "ghcr"],
        },
    ];
}
