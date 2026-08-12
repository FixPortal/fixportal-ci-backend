using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReadyToMergeCalculatorTests
{
    private static readonly IReadOnlySet<string> Bots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dependabot[bot]",
    };

    private static readonly IReadOnlySet<string> NoBots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static PullRequest Pr(string author = "chris-fixportal", IReadOnlyList<ReviewSignal>? signals = null) =>
        new(
            181,
            "Title",
            author,
            "https://github.com/FixPortal/repo/pull/181",
            false,
            Instant.FromUnixTimeSeconds(0),
            signals
        );

    private static ReviewSignal Signal(ReviewSignalState state) => new("CodeRabbit", state, null, null);

    private static PrMergeState Merge(
        string? mergeable = "MERGEABLE",
        string? status = "CLEAN",
        bool isDraft = false
    ) => new(181, isDraft, mergeable, status);

    [Fact]
    public void Clean_merge_state_and_clean_signals_are_ready()
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), Merge(), true, NoBots)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void An_empty_signal_list_is_ready_rather_than_unknown()
    {
        // What a private repository now produces when every configured reviewer is
        // PublicOnly: an empty list, not a null one. Empty means "the enrichment ran and
        // no reviewer applies here", which is ready; null means "enrichment has not
        // reached this pull request", which is unknown. Collapsing the two would restore
        // the estate-wide vanished-pill bug in a different disguise.
        ReadyToMergeCalculator.Evaluate(Pr(signals: []), Merge(), true, NoBots).Should().BeTrue();
    }

    [Fact]
    public void Disabled_signals_do_not_block()
    {
        ReadyToMergeCalculator
            .Evaluate(
                Pr(signals: [Signal(ReviewSignalState.Clean), Signal(ReviewSignalState.Disabled)]),
                Merge(),
                true,
                NoBots
            )
            .Should()
            .BeTrue();
    }

    // Pending is the load-bearing case: it means a required reviewer left no evidence it
    // ran. Treating it as benign would surface exactly the pull requests nobody checked.
    [Theory]
    [InlineData(ReviewSignalState.Outstanding)]
    [InlineData(ReviewSignalState.Pending)]
    public void Outstanding_or_pending_signals_are_not_ready(ReviewSignalState state)
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean), Signal(state)]), Merge(), true, NoBots)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData("CONFLICTING", "DIRTY")]
    [InlineData("MERGEABLE", "BLOCKED")]
    [InlineData("MERGEABLE", "BEHIND")]
    // Mergeable, but a non-required check is failing. A filter whose point is "safe to
    // merge" should not surface it.
    [InlineData("MERGEABLE", "UNSTABLE")]
    public void Unclean_merge_state_is_not_ready(string mergeable, string status)
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), Merge(mergeable, status), true, NoBots)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void A_draft_is_not_ready_however_clean()
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), Merge(isDraft: true), true, NoBots)
            .Should()
            .BeFalse();
    }

    // Undetermined must NOT collapse to "not ready": a freshly-opened pull request would
    // drop out of the filter for no visible reason and the filter would stop being trusted.
    [Theory]
    [InlineData("UNKNOWN", "CLEAN")]
    [InlineData("MERGEABLE", "UNKNOWN")]
    [InlineData("MERGEABLE", null)]
    public void Undetermined_merge_state_is_unknown(string? mergeable, string? status)
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), Merge(mergeable, status), true, NoBots)
            .Should()
            .BeNull();
    }

    [Fact]
    public void Missing_merge_state_is_unknown()
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), null, true, NoBots)
            .Should()
            .BeNull();
    }

    // The three meanings of "no signals", which is the whole reason this lives server-side.

    [Fact]
    public void No_signals_with_reviewers_configured_is_unknown()
    {
        ReadyToMergeCalculator.Evaluate(Pr(), Merge(), true, NoBots).Should().BeNull();
    }

    [Fact]
    public void No_signals_with_no_reviewers_configured_is_ready()
    {
        ReadyToMergeCalculator.Evaluate(Pr(), Merge(), false, NoBots).Should().BeTrue();
    }

    // A green dependency bump is the single most useful thing this filter surfaces, and it
    // carries no signals by policy. Reading that absence as "not yet reviewed" would hide it.
    [Fact]
    public void An_excluded_bot_author_needs_no_review()
    {
        ReadyToMergeCalculator.Evaluate(Pr("dependabot[bot]"), Merge(), true, Bots).Should().BeTrue();
    }

    [Fact]
    public void Excluded_authors_match_case_insensitively()
    {
        ReadyToMergeCalculator.Evaluate(Pr("Dependabot[Bot]"), Merge(), true, Bots).Should().BeTrue();
    }

    // Exempt from REVIEW, not from GitHub's verdict.
    [Fact]
    public void An_excluded_bot_with_conflicts_is_still_not_ready()
    {
        ReadyToMergeCalculator
            .Evaluate(Pr("dependabot[bot]"), Merge("CONFLICTING", "DIRTY"), true, Bots)
            .Should()
            .BeFalse();
    }

    // An unrecognised enum member from a newer GitHub must fall through to "not ready"
    // rather than be treated as clean.
    [Fact]
    public void An_unrecognised_merge_state_is_not_ready()
    {
        ReadyToMergeCalculator
            .Evaluate(Pr(signals: [Signal(ReviewSignalState.Clean)]), Merge(status: "SOMETHING_NEW"), true, NoBots)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void An_empty_signal_list_is_ready()
    {
        ReadyToMergeCalculator.Evaluate(Pr(signals: []), Merge(), true, NoBots).Should().BeTrue();
    }
}
