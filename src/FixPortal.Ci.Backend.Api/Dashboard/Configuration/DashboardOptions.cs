namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

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
    public string MetricsWorkDirectory { get; init; } = ""; // "" -> temp subdir
    public bool MergedPrEnabled { get; init; } = true;
    public int MergedPrRefreshSeconds { get; init; } = 300;

    // Compiled fallback lanes, used when no Dashboard:JobLanes are configured. These
    // are deliberately NOT the default of the bound JobLanes property below: the
    // configuration binder APPENDS bound collection items to a pre-populated list
    // rather than replacing it, so keeping defaults there would leave every
    // configured lane shadowed behind the compiled default of the same Key
    // (FirstOrDefault(Key) returns the default first). Consume GetEffectiveJobLanes().
    public static readonly IReadOnlyList<JobLaneOptions> DefaultJobLanes =
    [
        new()
        {
            Key = "deploys",
            Label = "Deploys",
            Patterns = ["deploy"],
        },
        new()
        {
            Key = "packages",
            Label = "Packages",
            Patterns = ["publish", "package", "docker", "image", "release", "ghcr"],
        },
    ];

    // Bound from Dashboard:JobLanes. Empty by default (see DefaultJobLanes) so the
    // binder replaces rather than appends. Consumers must use GetEffectiveJobLanes().
    public IReadOnlyList<JobLaneOptions> JobLanes { get; init; } = [];

    // The lanes actually in force: the configured lanes when any are bound, otherwise
    // the compiled defaults. De-duplicated by Key (last occurrence wins) so a repeated
    // key resolves to the last configured entry rather than the first.
    public IReadOnlyList<JobLaneOptions> GetEffectiveJobLanes() =>
        JobLanes.Count == 0
            ? DefaultJobLanes
            : JobLanes.GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
}
