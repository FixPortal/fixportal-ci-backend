namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

/// <summary>Where a reviewer's state is read from.</summary>
public enum ReviewerSource
{
    /// <summary>Unresolved review threads whose first comment is authored by BotLogin.</summary>
    ReviewThreads,

    /// <summary>Open code-scanning alerts on the pull request's head ref.</summary>
    CodeScanning,
}

public sealed class ReviewerOptions
{
    /// <summary>Display label shown on the pill, e.g. "CodeRabbit".</summary>
    public required string Name { get; init; }

    /// <summary>GitHub login of the reviewing bot. Required for ReviewThreads reviewers.</summary>
    public string? BotLogin { get; init; }

    /// <summary>
    /// When set, this reviewer is Disabled on any pull request that does not carry the
    /// label. Absent means the reviewer applies to every pull request.
    /// </summary>
    public string? RequiredLabel { get; init; }

    public ReviewerSource Source { get; init; } = ReviewerSource.ReviewThreads;
}

public sealed class ReviewSignalsOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sweep cadence. 900s, NOT the 150s this shipped with — 150s was unpayable and the
    /// production logs proved it. GraphQL bills 5,000 POINTS/hour (not requests), the
    /// sweep measured 1,537 points across 29 repos (~53 each), and 24 sweeps/hour wanted
    /// 36,888 — 7.4x the budget. The observed result: three sweeps landed, the fourth
    /// died part-way, and the remaining ~51 minutes of every hour returned rate-limit
    /// errors that <see cref="ReviewSignalsOptions"/>' worker converts to last-known-good.
    /// The board looked fine while 22 of 29 repos served stale pills for most of the hour.
    /// The budget is also shared with any human using the same PAT, so an exhausted hour
    /// blocks `gh` at the terminal too — that is how this was found.
    /// 900s with the halved PR cap is ~4 sweeps/hour, leaving headroom rather than
    /// spending to zero. Re-measure before raising it: the worker logs the real cost per
    /// sweep, so this is checkable rather than a guess.
    /// </summary>
    public int RefreshSeconds { get; init; } = 900;

    /// <summary>
    /// Pull request authors whose PRs get no review signals at all — dependency bots,
    /// which are out of AI code review by policy. Matched case-insensitively.
    /// </summary>
    public IReadOnlyList<string> ExcludedAuthors { get; init; } = [];

    /// <summary>
    /// The reviewers to report. EMPTY BY DEFAULT and deliberately so: the configuration
    /// binder APPENDS bound collection items to a pre-populated list rather than
    /// replacing it, so a compiled-in default would shadow every configured entry (the
    /// same trap documented on DashboardOptions.JobLanes). Empty also means the whole
    /// feature is off, and the worker issues no requests.
    /// </summary>
    public IReadOnlyList<ReviewerOptions> Reviewers { get; init; } = [];
}

internal static class ReviewSignalsOptionsRegistration
{
    public static void AddReviewSignalsOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ReviewSignalsOptions>()
            .Bind(configuration.GetSection("ReviewSignals"))
            .Validate(o => o.RefreshSeconds > 0, "ReviewSignals:RefreshSeconds must be greater than zero.")
            .Validate(
                o => o.Reviewers.All(r => !string.IsNullOrWhiteSpace(r.Name)),
                "Every ReviewSignals:Reviewers entry must set a non-blank Name (it is the pill's label)."
            )
            .Validate(
                o =>
                    o.Reviewers.All(r =>
                        r.Source != ReviewerSource.ReviewThreads || !string.IsNullOrWhiteSpace(r.BotLogin)
                    ),
                "Every ReviewSignals:Reviewers entry with Source=ReviewThreads must set a non-blank BotLogin, or it can never match and reports Pending forever."
            )
            .ValidateOnStart();
    }
}
