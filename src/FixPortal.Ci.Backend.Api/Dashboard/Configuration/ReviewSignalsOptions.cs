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

    /// <summary>
    /// Open secret-scanning alerts on the repository. Repository-scoped, not pull-request
    /// scoped: the alerts endpoint takes no ref filter, so every open pull request in a
    /// repository with an open secret alert reports the same Outstanding count.
    /// </summary>
    SecretScanning,
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
    /// forever, because a comment is neither a review nor a thread. The presence of a
    /// comment is the whole signal -- its content is never inspected, so a status update
    /// or a "paused, resuming later" comment counts as a pass exactly like a genuine
    /// all-clear.
    /// </summary>
    public bool CommentsCountAsParticipation { get; init; }

    /// <summary>
    /// When true, a successful code-scanning check on the head commit is itself evidence
    /// this reviewer ran, even though it left no review, thread or comment.
    /// </summary>
    /// <remarks>
    /// For GitHub Code Quality, which is delivered BY the code-scanning pipeline — its
    /// workflow run is named "Code Quality: PR #n" but its path is
    /// <c>dynamic/github-code-scanning/codeql</c> — while publishing its findings as
    /// review threads rather than alerts. It says nothing at all when it finds nothing,
    /// so every other participation channel here reads a clean pull request exactly like
    /// a reviewer that never ran, and the pill would hold Pending on precisely the pull
    /// requests that are ready to merge. The check is the missing evidence: it only
    /// succeeds once the pipeline has actually completed on this head.
    ///
    /// Narrow on purpose. This says "the scan ran", never "the scan was happy" — the
    /// unresolved-thread check above still decides Outstanding, and runs first, so a
    /// green check can never mask an open finding.
    /// </remarks>
    public bool CodeScanningCheckCountsAsParticipation { get; init; }

    /// <summary>
    /// When true, this reviewer applies to public repositories only and is omitted
    /// entirely from a private repository's signals — no pill, and nothing for the
    /// ready-to-merge verdict to wait on.
    /// </summary>
    /// <remarks>
    /// For GitHub's own scanning products, which are paid on private repositories and
    /// free on public ones. With them switched off org-wide (2026-08-04) their endpoints
    /// answer 403/404 on every private repository, which this factory reads as Pending —
    /// correct in isolation, but Pending is not a state a private repository can ever
    /// leave, so it pinned every private pull request to "not ready" and the Ready to
    /// merge pill disappeared estate-wide. Omitting beats reporting Disabled here: a
    /// permanently grey pill on 20 of 28 repositories is noise, and absence is documented
    /// in the board's legend.
    /// </remarks>
    public bool PublicOnly { get; init; }

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
