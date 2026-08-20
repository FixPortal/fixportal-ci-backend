using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Turns one pull request's observed facts into a per-reviewer state. Pure and
/// reviewer-agnostic: the reviewers are configuration, so no FixPortal review policy
/// is compiled in.
///
/// The ordering below is the whole design. Disabled is decided first, so a reviewer
/// that does not apply here never shows red. Clean requires POSITIVE evidence that the
/// reviewer ran: without that rule, a paused reviewer and a reviewer that finished
/// cleanly both present as zero unresolved threads, and the board would report a pass
/// nobody performed.
/// </summary>
public static class ReviewSignalFactory
{
    // The GitHub Apps that publish CodeQL and other code-scanning results. "github-advanced-security"
    // is what GitHub actually emits today (verified 2026-08-03 by GraphQL against live PRs across
    // three repos -- not one instance of the other slug in real data). "github-code-scanning" is the
    // historical slug, kept in case a repo or enterprise hasn't moved to the newer branding. Matching
    // both is strictly safer than betting on either. Live bug: this used to be a single const pinned
    // to the wrong slug, so the CodeQL pill rendered Pending forever regardless of scan outcome.
    private static readonly string[] CodeScanningAppSlugs = ["github-advanced-security", "github-code-scanning"];

    public static IReadOnlyList<ReviewSignal> Build(
        PrReviewFacts facts,
        IReadOnlyList<ReviewerOptions> reviewers,
        int? openAlerts,
        int? openSecretAlerts,
        string prHtmlUrl,
        string repoHtmlUrl
    )
    {
        var signals = new List<ReviewSignal>(reviewers.Count);
        foreach (var reviewer in reviewers)
        {
            signals.Add(BuildOne(facts, reviewer, openAlerts, openSecretAlerts, prHtmlUrl, repoHtmlUrl));
        }
        return signals;
    }

    private static ReviewSignal BuildOne(
        PrReviewFacts facts,
        ReviewerOptions reviewer,
        int? openAlerts,
        int? openSecretAlerts,
        string prHtmlUrl,
        string repoHtmlUrl
    )
    {
        // Trimmed: a stray space in configuration ("review-high ") matches no real label,
        // which would pin the reviewer to Disabled forever — and a human reads Disabled
        // as "safe to skip", not as "misconfigured".
        var label = reviewer.RequiredLabel?.Trim();
        if (!string.IsNullOrEmpty(label) && !facts.Labels.Contains(label))
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Disabled, null, null);
        }

        return reviewer.Source switch
        {
            ReviewerSource.CodeScanning => BuildCodeScanning(facts, reviewer, openAlerts, prHtmlUrl),
            ReviewerSource.SecretScanning => BuildSecretScanning(reviewer, openSecretAlerts, repoHtmlUrl),
            _ => BuildReviewThreads(facts, reviewer, prHtmlUrl),
        };
    }

    /// <summary>
    /// Repository-scoped, unlike every other source here: the secret-scanning alerts
    /// endpoint takes no ref filter, so an open alert anywhere in the repository shows on
    /// every open pull request in it. That is a coarser claim than the other pills make,
    /// and it is the true one — a leaked credential is not fixed by merging elsewhere.
    /// </summary>
    /// <remarks>
    /// Zero alerts is Clean without the "did it run" check the code-scanning path needs,
    /// because here the endpoint answering IS that evidence: GitHub 404s the route when
    /// secret scanning is disabled, and scanning is continuous rather than per-commit, so
    /// there is no per-pull-request run to wait for.
    /// </remarks>
    internal static ReviewSignal BuildSecretScanning(
        ReviewerOptions reviewer,
        int? openSecretAlerts,
        string repoHtmlUrl
    )
    {
        // Unreadable (scanning disabled, or the token lacks the scope) is unknown, not clean.
        if (openSecretAlerts is not { } alerts)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }
        return alerts > 0
            ? new ReviewSignal(
                reviewer.Name,
                ReviewSignalState.Outstanding,
                alerts,
                $"{repoHtmlUrl}/security/secret-scanning"
            )
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null);
    }

    private static ReviewSignal BuildCodeScanning(
        PrReviewFacts facts,
        ReviewerOptions reviewer,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        // Unreadable alerts (no permission, scanning disabled) are unknown, not clean.
        if (openAlerts is not { } alerts)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }
        if (alerts > 0)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Outstanding, alerts, $"{prHtmlUrl}/checks");
        }
        // Zero alerts only means clean once a scan has actually completed for this PR.
        return facts.SuccessfulCheckAppSlugs.Overlaps(CodeScanningAppSlugs)
            ? new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null)
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
    }

    private static ReviewSignal BuildReviewThreads(PrReviewFacts facts, ReviewerOptions reviewer, string prHtmlUrl)
    {
        // A reviewer with no configured login can never be matched. Report Pending
        // rather than Clean so a configuration mistake surfaces as "not reviewed"
        // instead of a green pill nobody earned.
        if (reviewer.BotLogin is not { Length: > 0 } login)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }

        var unresolved = facts.UnresolvedThreadsByAuthor.GetValueOrDefault(login);
        if (unresolved > 0)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Outstanding, unresolved, $"{prHtmlUrl}/files");
        }

        // A truncated reviewThreads connection means the unresolved count above is a
        // lower bound, not a count: a thread past the page cap is simply absent, so
        // nothing below can prove this reviewer clean. Hold at Pending until the pull
        // request fits the query again.
        if (facts.TruncatedConnections?.Contains(GitHubOrgClient.ReviewThreadsConnectionName) == true)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }

        // A passing check is not evidence a reviewer ran: CodeRabbit's "rate limited" /
        // "review skipped" checks pass by design so they never block a protected-branch
        // merge. Participation is a review, an opened thread, or -- for a reviewer opted
        // into CommentsCountAsParticipation -- a head-scoped issue comment. The comment
        // channel exists because a reviewer that reports findings as threads but
        // announces a clean result as a plain comment (Gitar) would
        // otherwise be pinned to Pending forever. This sits AFTER the unresolved-thread
        // return above, so a comment can only ever promote Pending to Clean -- it can
        // never mask an open finding.
        // The third channel is for a reviewer that says nothing at all when it finds
        // nothing (Code Quality). It is delivered by the code-scanning pipeline, so a
        // successful code-scanning check on this head proves it ran; without this it
        // reads identically to a reviewer that never ran, and holds Pending on exactly
        // the pull requests that are ready to merge.
        var ran =
            facts.HeadParticipatingAuthors.Contains(login)
            || reviewer.CommentsCountAsParticipation && facts.HeadCommentAuthors.Contains(login)
            || reviewer.CodeScanningCheckCountsAsParticipation
                && facts.SuccessfulCheckAppSlugs.Overlaps(CodeScanningAppSlugs);
        return ran
            ? new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null)
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
    }
}
