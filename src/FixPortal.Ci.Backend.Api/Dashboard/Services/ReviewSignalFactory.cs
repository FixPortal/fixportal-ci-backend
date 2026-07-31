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
    // The GitHub App that publishes CodeQL and other code-scanning results.
    private const string CodeScanningAppSlug = "github-code-scanning";

    public static IReadOnlyList<ReviewSignal> Build(
        PrReviewFacts facts,
        IReadOnlyList<ReviewerOptions> reviewers,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        var signals = new List<ReviewSignal>(reviewers.Count);
        foreach (var reviewer in reviewers)
        {
            signals.Add(BuildOne(facts, reviewer, openAlerts, prHtmlUrl));
        }
        return signals;
    }

    private static ReviewSignal BuildOne(
        PrReviewFacts facts,
        ReviewerOptions reviewer,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        if (reviewer.RequiredLabel is { } label && !string.IsNullOrWhiteSpace(label) && !facts.Labels.Contains(label))
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Disabled, null, null);
        }

        return reviewer.Source == ReviewerSource.CodeScanning
            ? BuildCodeScanning(facts, reviewer, openAlerts, prHtmlUrl)
            : BuildReviewThreads(facts, reviewer, prHtmlUrl);
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
        return facts.SuccessfulCheckAppSlugs.Contains(CodeScanningAppSlug)
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

        // A passing check is not evidence a reviewer ran: CodeRabbit's "rate limited" /
        // "review skipped" checks pass by design so they never block a protected-branch
        // merge. Only actual participation (a review or an opened thread) counts.
        var ran = facts.HeadParticipatingAuthors.Contains(login);
        return ran
            ? new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null)
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
    }
}
