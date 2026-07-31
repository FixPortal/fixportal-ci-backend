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
    public int RefreshSeconds { get; init; } = 150;

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
