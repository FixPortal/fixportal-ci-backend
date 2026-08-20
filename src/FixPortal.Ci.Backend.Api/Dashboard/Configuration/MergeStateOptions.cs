namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

/// <summary>
/// Settings for the merge-state enrichment that backs the board's "ready to merge" filter.
/// </summary>
public sealed class MergeStateOptions
{
    /// <summary>
    /// On by default, unlike review signals. There is nothing to configure for it to be
    /// meaningful — no reviewer roster, no per-repo setup — and the cost is a GraphQL
    /// point per repository per sweep, most of which is avoided by the conditional
    /// open-PR listing answering 304 for a quiet repository.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sweep cadence. Short on purpose: merge state has no terminal value, because another
    /// pull request merging advances the base branch and can invalidate every open pull
    /// request in the repository at once, without touching any of their head SHAs or
    /// updated_at timestamps. Nothing cheap can observe that, so the answer is to re-ask
    /// often rather than to be clever about when.
    /// </summary>
    public int RefreshSeconds { get; init; } = 120;

    public static void AddMergeStateOptions(IServiceCollection services, IConfiguration configuration) =>
        services
            .AddOptions<MergeStateOptions>()
            .Bind(configuration.GetSection("MergeState"))
            .Validate(o => o.RefreshSeconds > 0, "MergeState:RefreshSeconds must be greater than zero.")
            .ValidateOnStart();
}
