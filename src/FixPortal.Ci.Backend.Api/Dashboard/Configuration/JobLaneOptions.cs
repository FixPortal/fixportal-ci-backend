namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

/// <summary>
/// One configurable "job lane" — a named set of job-name patterns the lane worker
/// matches inside each repo's workflows (e.g. deploys, packages). Decouples the
/// enrichment mechanism from the specific signal it surfaces.
/// </summary>
public sealed class JobLaneOptions
{
    public required string Key { get; init; }        // stable id + DI key, e.g. "deploys"
    public required string Label { get; init; }      // display label, e.g. "Deploys"
    public bool Enabled { get; init; } = true;
    public int RefreshSeconds { get; init; } = 300;
    public IReadOnlyList<string> Patterns { get; init; } = [];
    public int MaxRunsToScan { get; init; } = 30;
}
