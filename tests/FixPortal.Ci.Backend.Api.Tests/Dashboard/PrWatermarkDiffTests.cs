using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// The diff is the whole redesign in one function: the old sweep asked 25 PR slots per
// repository on a timer and paid full GraphQL fan-out for every empty slot (measured:
// 1,537 points per sweep across 29 repositories, to track 2 open pull requests). This
// decides which pull requests actually need paying for, from a REST listing that costs
// nothing when unchanged.
public class PrWatermarkDiffTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 8, 2, 12, 0, 0);

    private static Dictionary<int, PrWatermark> Watermarks(params (int Number, PrWatermark Mark)[] entries) =>
        entries.ToDictionary(e => e.Number, e => e.Mark);

    [Fact]
    public void An_unchanged_listing_marks_nothing_dirty_so_a_quiet_sweep_spends_nothing()
    {
        var same = Watermarks((181, new PrWatermark(T0, "abc")), (182, new PrWatermark(T0, "def")));

        var diff = PrWatermarkDiff.Compute(
            same,
            Watermarks((181, new PrWatermark(T0, "abc")), (182, new PrWatermark(T0, "def")))
        );

        _ = diff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_pull_request_absent_from_the_previous_listing_is_dirty()
    {
        var diff = PrWatermarkDiff.Compute(Watermarks(), Watermarks((181, new PrWatermark(T0, "abc"))));

        _ = diff.Dirty.Should().Equal(181);
        _ = diff.Evicted.Should().BeEmpty();
    }

    [Fact]
    public void A_moved_head_invalidates_even_when_the_timestamp_is_unchanged()
    {
        // Review state is only meaningful relative to a head: a reviewer's approval of
        // the previous commit says nothing about what is on the pull request now.
        var previous = Watermarks((181, new PrWatermark(T0, "abc")));
        var current = Watermarks((181, new PrWatermark(T0, "zzz")));

        _ = PrWatermarkDiff.Compute(previous, current).Dirty.Should().Equal(181);
    }

    [Fact]
    public void A_bumped_timestamp_invalidates_even_when_the_head_is_unchanged()
    {
        // A review, a review comment or a label change moves updated_at without a push,
        // and all three can change a pill.
        var previous = Watermarks((181, new PrWatermark(T0, "abc")));
        var current = Watermarks((181, new PrWatermark(T0.Plus(Duration.FromMinutes(1)), "abc")));

        _ = PrWatermarkDiff.Compute(previous, current).Dirty.Should().Equal(181);
    }

    [Fact]
    public void A_pull_request_that_has_gone_is_evicted_rather_than_refetched()
    {
        // Eviction is free and fixes a pill outliving the pull request it described.
        var diff = PrWatermarkDiff.Compute(Watermarks((181, new PrWatermark(T0, "abc"))), Watermarks());

        _ = diff.Evicted.Should().Equal(181);
        _ = diff.Dirty.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_watermark_is_treated_as_dirty_rather_than_trusted()
    {
        // If the listing gave us neither field we cannot prove the cached signal still
        // holds. Spending a query is recoverable and bounded by the reserve floor;
        // certifying a stale pill is not.
        var unknown = Watermarks((181, new PrWatermark(null, null)));

        _ = PrWatermarkDiff.Compute(unknown, unknown).Dirty.Should().Equal(181);
    }
}

// A pull request the token cannot read must stay dirty. Advancing its watermark to the
// value just observed would report it as up to date forever, while every other pull
// request in the repo kept working — the partial, harder-to-notice version of certifying
// stale state.
public class FailedWatermarkRetentionTests
{
    private static readonly Instant T0 = Instant.FromUtc(2026, 8, 2, 12, 0, 0);

    [Fact]
    public void A_refused_pull_request_keeps_its_previous_watermark_so_it_stays_dirty()
    {
        var previous = new Dictionary<int, PrWatermark> { [181] = new(T0, "old") };
        var current = new Dictionary<int, PrWatermark>
        {
            [181] = new(T0.Plus(Duration.FromMinutes(5)), "new"),
            [182] = new(T0, "other"),
        };

        var committed = ReviewSignalEnrichmentWorker.WithoutFailed(current, previous, [181]);

        _ = committed[181].Should().Be(new PrWatermark(T0, "old"));
        _ = committed[182].Should().Be(new PrWatermark(T0, "other"));
        // The whole point: the next diff must still see 181 as changed.
        _ = PrWatermarkDiff.Compute(committed, current).Dirty.Should().Equal(181);
    }

    [Fact]
    public void A_refused_pull_request_never_seen_before_is_dropped_rather_than_recorded()
    {
        // Nothing to fall back to, so recording anything would certify a pull request
        // whose facts were never read. Absent means "unknown", which reads as dirty.
        var current = new Dictionary<int, PrWatermark> { [181] = new(T0, "new") };

        var committed = ReviewSignalEnrichmentWorker.WithoutFailed(current, new Dictionary<int, PrWatermark>(), [181]);

        _ = committed.Should().BeEmpty();
        _ = PrWatermarkDiff.Compute(committed, current).Dirty.Should().Equal(181);
    }
}

public class ReviewSignalPriorityTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 2, 12, 0, 0);
    private static readonly Duration Interval = Duration.FromMinutes(10);

    private static IReadOnlyDictionary<int, CachedReviewSignals> Signals(ReviewSignalState state) =>
        new Dictionary<int, CachedReviewSignals>
        {
            [181] = new("sha", [new ReviewSignal("Gitar", state, null, null)]),
        };

    [Theory]
    // Waiting on a human or a bot: both can flip without the watermark moving, because
    // resolving a thread and a check completing on an unchanged head bump nothing.
    [InlineData(ReviewSignalState.Outstanding, true)]
    [InlineData(ReviewSignalState.Pending, true)]
    // Terminal for this head. Clean can only be undone by a push, and a push moves the
    // head, which the free watermark diff already catches.
    [InlineData(ReviewSignalState.Clean, false)]
    [InlineData(ReviewSignalState.Disabled, false)]
    public void Only_non_terminal_pills_are_worth_refetching_blind(ReviewSignalState state, bool expected)
    {
        var stale = new Dictionary<int, Instant> { [181] = Now - Interval };

        var due = ReviewSignalPriority.SelectDue(Signals(state), stale, Interval, Now);

        _ = due.Any().Should().Be(expected);
    }

    [Fact]
    public void A_terminal_pill_is_still_reconciled_eventually()
    {
        // Clean is terminal for a head, but not permanently trustworthy: a check can
        // re-run and fail against an unchanged head, which moves neither updated_at nor
        // the head SHA. Nothing free observes that, so it is caught on the slow pass.
        var longAgo = new Dictionary<int, Instant> { [181] = Now - Duration.FromHours(2) };

        _ = ReviewSignalPriority
            .SelectDue(Signals(ReviewSignalState.Clean), longAgo, Interval, Now)
            .Should()
            .Equal(181);
    }

    [Fact]
    public void A_recently_observed_pill_is_left_alone_until_the_interval_elapses()
    {
        var justSeen = new Dictionary<int, Instant> { [181] = Now - Duration.FromMinutes(1) };

        _ = ReviewSignalPriority
            .SelectDue(Signals(ReviewSignalState.Outstanding), justSeen, Interval, Now)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_cached_pill_with_no_observation_is_refetched_rather_than_assumed_fresh()
    {
        _ = ReviewSignalPriority
            .SelectDue(Signals(ReviewSignalState.Pending), new Dictionary<int, Instant>(), Interval, Now)
            .Should()
            .Equal(181);
    }
}
