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

    /// <summary>
    /// When set, an issue comment from <see cref="BotLogin"/> dated after the head commit
    /// also counts as participation. For reviewers that report findings as review threads
    /// but announce a clean result as a plain comment: without this they hold Pending
    /// forever, because a comment is neither a review nor a thread.
    /// </summary>
    public bool CommentsCountAsParticipation { get; init; }

    public ReviewerSource Source { get; init; } = ReviewerSource.ReviewThreads;
}

public sealed class ReviewSignalsOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sweep cadence. Back to 150s, and affordable this time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// History, because the number has moved twice and the reasons matter. It shipped at
    /// 150s against a per-repo GraphQL sweep costing 1,537 points across 29 repositories;
    /// GraphQL bills 5,000 POINTS/hour (not requests), so 24 sweeps/hour wanted 36,888 —
    /// 7.4x the budget. Three sweeps landed each hour, the fourth died part-way, and the
    /// remaining ~51 minutes returned rate-limit errors the worker converts to
    /// last-known-good. It was briefly raised to 900s to fit.
    /// </para>
    /// <para>
    /// A sweep no longer costs points. Discovery is a conditional REST request that
    /// answers 304 when nothing changed, and GraphQL is spent only on pull requests whose
    /// watermark actually moved — so cadence now buys responsiveness rather than spending
    /// budget, and the compromise value is no longer justified. What it still costs is one
    /// conditional REST request per repository per sweep, which is why this is 150s and
    /// not the board's 20s.
    /// </para>
    /// <para>
    /// This also sets the cache TTL, which <c>Program.cs</c> derives as 3x this value.
    /// That is now a sensible coupling: every sweep re-verifies and rewrites, so the TTL
    /// means "the worker has checked this repository recently", not "these bytes are
    /// young".
    /// </para>
    /// </remarks>
    public int RefreshSeconds { get; init; } = 150;

    /// <summary>
    /// GraphQL points left unspent for everything else on this identity. GitHub meters
    /// the 5,000/hour budget PER USER, not per token, so this worker's PAT shares a pool
    /// with any human running `gh` — and spending it to zero blocks them mid-task, with
    /// an error naming a numeric user ID rather than this dashboard.
    /// Now that spend tracks pull-request activity rather than estate size, this should
    /// never fire. That is the point of keeping it: it is the alarm, not the mechanism.
    /// If a sweep starts skipping repositories for budget, something has regressed —
    /// a watermark that never matches, an unknown-watermark loop, or a query whose cost
    /// grew — and the Warning it logs is the first sign. Zero disables it and restores
    /// the original drain-to-empty behaviour.
    /// </summary>
    public int ReserveBudgetPoints { get; init; } = 1000;

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
                o => o.ReserveBudgetPoints >= 0,
                "ReviewSignals:ReserveBudgetPoints must not be negative (0 disables the reserve)."
            )
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
            .Validate(
                o => o.Reviewers.All(r => !r.CommentsCountAsParticipation || !string.IsNullOrWhiteSpace(r.BotLogin)),
                "Every ReviewSignals:Reviewers entry with CommentsCountAsParticipation=true must set a non-blank BotLogin, or it can never match and reports Pending forever."
            )
            .ValidateOnStart();
    }
}
