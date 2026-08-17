using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Decides whether a pull request is ready to merge: GitHub says it can be merged with
/// nothing outstanding, AND every configured reviewer is either clean or not required.
/// </summary>
/// <remarks>
/// <para>
/// The verdict is computed here rather than on the client because only the server can
/// tell the three meanings of "this pull request has no review signals" apart — the
/// feature is switched off, the author is an excluded dependency bot, or enrichment has
/// simply not reached it yet. Those are "ready", "ready" and "unknown" respectively, and
/// they are indistinguishable in the wire shape the client receives.
/// </para>
/// <para>
/// Tri-state on purpose. Collapsing "not yet known" into "not ready" would make a
/// freshly-opened pull request drop out of the board's ready filter for a minute or two
/// with nothing on screen explaining why, and the user would learn to distrust the filter.
/// </para>
/// </remarks>
public static class ReadyToMergeCalculator
{
    /// <summary>
    /// True ready, false not ready, null not yet determined.
    /// </summary>
    /// <param name="pr">The pull request, carrying its review signals if any were cached.</param>
    /// <param name="mergeState">GitHub's merge verdict, or null if not yet collected.</param>
    /// <param name="reviewsConfigured">
    /// Whether any reviewer is configured at all. With none, reviewers cannot gate
    /// anything, so the review half is vacuously satisfied rather than permanently unknown
    /// — otherwise a deployment with review signals switched off would show an empty
    /// ready filter forever.
    /// </param>
    /// <param name="excludedAuthors">
    /// Authors exempt from review by policy — the dependency bots. Their pull requests
    /// carry no signals by design, so absence here means "no review required", not
    /// "not yet reviewed". Getting this backwards would hide exactly the pull requests
    /// this filter is most useful for.
    /// </param>
    public static bool? Evaluate(
        PullRequest pr,
        PrMergeState? mergeState,
        bool reviewsConfigured,
        IReadOnlySet<string> excludedAuthors
    )
    {
        // Merge state first: it is the cheaper judgement and the more decisive one.
        if (mergeState is null || mergeState.IsUndetermined)
        {
            return null;
        }

        // A merge verdict is earned against one head commit. If the cached verdict's head
        // does not match the head the board currently sees — a push landed between the
        // merge-state sweep and now — the verdict says nothing about this head, so the
        // pull request is unknown, not ready. Either side missing its SHA is the same:
        // it cannot prove currency, so it must not certify readiness.
        if (!DashboardRefreshService.SameHead(pr.HeadSha, mergeState.HeadSha))
        {
            return null;
        }

        if (!mergeState.IsMergeClean)
        {
            return false;
        }

        if (!reviewsConfigured || excludedAuthors.Contains(pr.Author))
        {
            return true;
        }

        if (pr.ReviewSignals is not { } signals)
        {
            return null;
        }

        // Clean and Disabled are the only states that let a merge through. Pending is NOT
        // benign: it means a required reviewer has left no evidence it ran, which is the
        // whole reason the pills distinguish it from Clean.
        return signals.All(s => s.State is ReviewSignalState.Clean or ReviewSignalState.Disabled);
    }
}
