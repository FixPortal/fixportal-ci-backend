using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ApplyReadyToMergeTests
{
    private const string Head = "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3";

    private static readonly IReadOnlySet<string> NoBots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static PullRequest Pr(int number, IReadOnlyList<ReviewSignal>? signals = null, string? headSha = Head) =>
        new(
            number,
            $"PR {number}",
            "chris-fixportal",
            $"https://github.com/FixPortal/repo/pull/{number}",
            false,
            Instant.FromUnixTimeSeconds(0),
            signals,
            HeadSha: headSha
        );

    private static PrMergeState Clean(int number, string? headSha = Head) =>
        new(number, false, "MERGEABLE", "CLEAN", headSha);

    [Fact]
    public void Stamps_each_pull_request_from_its_own_merge_state()
    {
        var prs = new[] { Pr(1, [new("CodeRabbit", ReviewSignalState.Clean, null, null)]), Pr(2, []) };
        var states = new Dictionary<int, PrMergeState>
        {
            [1] = Clean(1),
            [2] = new(2, false, "CONFLICTING", "DIRTY", Head),
        };

        var result = DashboardRefreshService.ApplyReadyToMerge(prs, states, true, NoBots);

        result.Should().HaveCount(2);
        result[0].ReadyToMerge.Should().BeTrue();
        result[1].ReadyToMerge.Should().BeFalse();
    }

    [Fact]
    public void A_pull_request_with_no_cached_merge_state_is_unknown()
    {
        var prs = new[] { Pr(1, []) };

        var result = DashboardRefreshService.ApplyReadyToMerge(prs, new Dictionary<int, PrMergeState>(), true, NoBots);

        result[0].ReadyToMerge.Should().BeNull();
    }

    [Fact]
    public void A_null_cache_leaves_every_pull_request_unknown()
    {
        var prs = new[] { Pr(1, []), Pr(2, []) };

        var result = DashboardRefreshService.ApplyReadyToMerge(prs, null, true, NoBots);

        result.Should().OnlyContain(pr => pr.ReadyToMerge == null);
    }

    [Fact]
    public void An_empty_pull_request_list_is_returned_unchanged()
    {
        DashboardRefreshService
            .ApplyReadyToMerge([], new Dictionary<int, PrMergeState>(), true, NoBots)
            .Should()
            .BeEmpty();
    }

    // H3: the merge-state cache is keyed by PR number, so without a head check a verdict
    // earned against head A would keep stamping ready after the push to head B, until the
    // merge-state worker's next sweep. The verdict must degrade to unknown instead.
    [Fact]
    public void A_merge_state_from_an_older_head_is_unknown_not_ready()
    {
        var prs = new[]
        {
            Pr(1, [new("CodeRabbit", ReviewSignalState.Clean, null, null)], headSha: new string('b', 40)),
        };
        var states = new Dictionary<int, PrMergeState> { [1] = Clean(1, headSha: new string('a', 40)) };

        var result = DashboardRefreshService.ApplyReadyToMerge(prs, states, true, NoBots);

        result[0].ReadyToMerge.Should().BeNull("the cached verdict was computed against a head this PR no longer has");
    }

    // Ordering guard. ApplyReadyToMerge reads the signals ApplyReviewSignals attaches, so
    // running them the other way round yields "unknown" for every pull request that had
    // signals -- which looks like the merge-state worker being broken, not a sequencing bug.
    [Fact]
    public void Reads_the_signals_attached_by_ApplyReviewSignals()
    {
        var signals = new Dictionary<int, CachedReviewSignals>
        {
            [1] = new(Head, [new("CodeRabbit", ReviewSignalState.Outstanding, 2, null)]),
        };
        var states = new Dictionary<int, PrMergeState> { [1] = Clean(1) };

        var withSignals = DashboardRefreshService.ApplyReviewSignals([Pr(1)], signals);
        var result = DashboardRefreshService.ApplyReadyToMerge(withSignals, states, true, NoBots);

        result[0].ReadyToMerge.Should().BeFalse("the outstanding signal must be visible to the calculator");
    }

    [Fact]
    public void Preserves_every_other_field()
    {
        var original = Pr(1, []);

        var result = DashboardRefreshService.ApplyReadyToMerge(
            [original],
            new Dictionary<int, PrMergeState> { [1] = Clean(1) },
            true,
            NoBots
        );

        result[0].Should().BeEquivalentTo(original, o => o.Excluding(pr => pr.ReadyToMerge));
    }
}
