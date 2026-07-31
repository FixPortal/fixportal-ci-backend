using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class DashboardRefreshServiceTests
{
    private const int CiTrendBuckets = 24;
    private const int MostRecentCiTrendIndex = CiTrendBuckets - 1;

    private static int CiTrendIndexForHoursAgo(int hoursAgo) => MostRecentCiTrendIndex - hoursAgo;

    private static RepositorySnapshot Repo(string name, params (SignalState State, string? Conclusion)[] wfs) =>
        new(
            name,
            $"https://github.com/FixPortal/{name}",
            true,
            wfs.Select(
                    (w, i) =>
                        new WorkflowSnapshot(
                            $"wf{i}",
                            $"wf{i}.yml",
                            w.State,
                            w.Conclusion is null
                                ? null
                                : new WorkflowRun(
                                    "completed",
                                    w.Conclusion,
                                    "url",
                                    "title",
                                    1,
                                    "main",
                                    "push",
                                    Instant.MinValue
                                )
                        )
                )
                .ToList(),
            [],
            null,
            null,
            null
        );

    [Fact]
    public void MergeWithPrevious_uses_fresh_when_no_previous()
    {
        var fresh = new[] { (Repo("a"), false), (Repo("b"), false) };
        _ = DashboardRefreshService.MergeWithPrevious(fresh, null).Should().HaveCount(2);
    }

    [Fact]
    public void MergeWithPrevious_keeps_prior_for_failed_repo()
    {
        var prior = new DashboardSnapshot(
            Instant.MinValue,
            "FixPortal",
            [Repo("a", (SignalState.Success, "success"))],
            [],
            null
        );
        var fresh = new[] { (Repo("a"), true) };
        var merged = DashboardRefreshService.MergeWithPrevious(fresh, prior);
        _ = merged[0].Workflows[0].State.Should().Be(SignalState.Success);
    }

    [Fact]
    public void MergeWithPrevious_strips_review_signals_from_a_reinstated_prior_snapshot()
    {
        // A repo whose fetch failed republishes its prior snapshot, and that substitution
        // chains forward every cycle. Workflow state is legitimately last-known-good but a
        // review signal is not: it was earned against a head commit that has since moved,
        // so a clean pill reinstated here is a pass nobody performed.
        var priorRepo = Repo("a", (SignalState.Success, "success")) with
        {
            PullRequests =
            [
                new PullRequest(
                    181,
                    "t",
                    "u",
                    "url",
                    false,
                    Instant.MinValue,
                    [new ReviewSignal("Gitar", ReviewSignalState.Clean, null, null)]
                ),
            ],
        };
        var prior = new DashboardSnapshot(Instant.MinValue, "FixPortal", [priorRepo], [], null);

        var merged = DashboardRefreshService.MergeWithPrevious([(Repo("a"), true)], prior);

        _ = merged[0].Workflows[0].State.Should().Be(SignalState.Success);
        _ = merged[0].PullRequests.Should().ContainSingle().Which.ReviewSignals.Should().BeNull();
    }

    [Fact]
    public void BuildSummary_counts_repos_workflows_failing_running_noci_and_prs()
    {
        var withPr = Repo("a", (SignalState.Success, "success"), (SignalState.Failure, "failure")) with
        {
            PullRequests = [new PullRequest(1, "t", "u", "url", false, Instant.MinValue)],
        };
        var repos = new[] { withPr, Repo("b", (SignalState.Running, null)), Repo("c") };
        var summary = DashboardRefreshService.BuildSummary(repos).ToDictionary(s => s.Key, s => s.Count);
        _ = summary["repos"].Should().Be(3);
        _ = summary["workflows"].Should().Be(3);
        _ = summary["failing"].Should().Be(1);
        _ = summary["running"].Should().Be(1);
        _ = summary["no-ci"].Should().Be(1);
        _ = summary["open-prs"].Should().Be(1);
    }

    [Fact]
    public void BuildSummary_tolerates_null_pull_requests_from_restored_old_snapshot()
    {
        // A snapshot restored from a pre-PR on-disk file deserializes PullRequests
        // to null; it must not throw when counted.
        var restored = Repo("old", (SignalState.Success, "success")) with
        {
            PullRequests = null!,
        };
        var summary = DashboardRefreshService.BuildSummary([restored]).ToDictionary(s => s.Key, s => s.Count);
        _ = summary["open-prs"].Should().Be(0);
    }

    [Fact]
    public void BuildSummary_counts_failing_and_running_deploys_and_tolerates_null_deploys()
    {
        var withDeploys = Repo("a", (SignalState.Success, "success")) with
        {
            Deploys =
            [
                new JobSignal("CI", "Deploy (prod)", SignalState.Failure, "u", Instant.MinValue),
                new JobSignal("CI", "Deploy (staging)", SignalState.Running, "u", Instant.MinValue),
                new JobSignal("CI", "Deploy (dev)", SignalState.Success, "u", Instant.MinValue),
            ],
        };
        var nullDeploys = Repo("b", (SignalState.Success, "success")) with { Deploys = null! };
        var summary = DashboardRefreshService
            .BuildSummary([withDeploys, nullDeploys])
            .ToDictionary(s => s.Key, s => s.Count);
        _ = summary["deploys-failing"].Should().Be(1);
        _ = summary["deploys-running"].Should().Be(1);
    }

    [Fact]
    public void BuildSummary_counts_failing_packages()
    {
        var withFailingPackage = Repo("a", (SignalState.Success, "success")) with
        {
            Packages =
            [
                new JobSignal("CI", "Publish Docker image", SignalState.Failure, "u", Instant.MinValue),
                new JobSignal("CI", "publish-demo-host", SignalState.Success, "u", Instant.MinValue),
            ],
        };
        var nullPackages = Repo("b", (SignalState.Success, "success")) with { Packages = null! };
        var summary = DashboardRefreshService
            .BuildSummary([withFailingPackage, nullPackages])
            .ToDictionary(s => s.Key, s => s.Count);
        _ = summary["packages-failing"].Should().Be(1);
    }

    [Fact]
    public void BuildSummary_splits_nloc_by_product_family()
    {
        var fp = Repo("fixportal-engine", (SignalState.Success, "success")) with
        {
            Metrics = new RepoMetrics(1000, 2.0, 50, 1, Instant.MinValue),
        };
        var qfn = Repo("fixportal-quickfixn", (SignalState.Success, "success")) with
        {
            Metrics = new RepoMetrics(250, 1.0, 10, 0, Instant.MinValue),
        };
        var summary = DashboardRefreshService.BuildSummary([fp, qfn, Repo("c")]).ToDictionary(s => s.Key, s => s.Count);
        _ = summary["nloc-fixportal"].Should().Be(1000);
        _ = summary["nloc-quickfixn"].Should().Be(250);
    }

    [Fact]
    public void PickLatestMerged_returns_the_newest_across_repos_or_null()
    {
        var a = new MergedPullRequest(1, "a", "u", "repoA", "ua", Instant.FromUnixTimeSeconds(100));
        var b = new MergedPullRequest(2, "b", "u", "repoB", "ub", Instant.FromUnixTimeSeconds(300));
        var c = new MergedPullRequest(3, "c", "u", "repoC", "uc", Instant.FromUnixTimeSeconds(200));
        _ = DashboardRefreshService.PickLatestMerged([a, null, b, c]).Should().BeSameAs(b);
        _ = DashboardRefreshService.PickLatestMerged([null, null]).Should().BeNull();
        _ = DashboardRefreshService.PickLatestMerged([]).Should().BeNull();
    }

    private static WorkflowRun RunAt(Instant updatedAt, string conclusion) =>
        new("completed", conclusion, "u", "t", 1, "main", "push", updatedAt);

    [Fact]
    public void BuildCiTrend_returns_24_oldest_first_NoData_for_empty()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var trend = DashboardRefreshService.BuildCiTrend([], now);
        _ = trend.Should().HaveCount(24);
        _ = trend[0].BucketStart.Should().Be(now - Duration.FromHours(24));
        _ = trend[23].BucketStart.Should().Be(now - Duration.FromHours(1));
        _ = trend.Should().OnlyContain(b => b.State == CiTrendState.NoData);
    }

    [Fact]
    public void BuildCiTrend_marks_an_hour_failing_when_any_run_failed()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var runs = new[]
        {
            RunAt(now - Duration.FromMinutes(20), "success"), // most recent hour
            RunAt(now - Duration.FromMinutes(40), "failure"), // same hour -> Failing wins
        };
        var trend = DashboardRefreshService.BuildCiTrend(runs, now);
        _ = trend[23].State.Should().Be(CiTrendState.Failing);
    }

    [Fact]
    public void BuildCiTrend_marks_an_hour_passing_when_runs_present_and_none_failed()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var trend = DashboardRefreshService.BuildCiTrend([RunAt(now - Duration.FromMinutes(20), "success")], now);
        _ = trend[23].State.Should().Be(CiTrendState.Passing);
    }

    [Fact]
    public void BuildCiTrend_quiet_hours_after_a_failure_are_NoData()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        // Failure 5h ago; hours with no runs show NoData, not Failing.
        var trend = DashboardRefreshService.BuildCiTrend(
            [RunAt(now - Duration.FromHours(5) - Duration.FromMinutes(10), "failure")],
            now
        );
        _ = trend[CiTrendIndexForHoursAgo(5)].State.Should().Be(CiTrendState.Failing); // the hour the failure landed
        _ = trend[CiTrendIndexForHoursAgo(4)].State.Should().Be(CiTrendState.NoData); // quiet hours after = NoData
        _ = trend[MostRecentCiTrendIndex].State.Should().Be(CiTrendState.NoData); // most recent quiet hour = NoData
        _ = trend[0].State.Should().Be(CiTrendState.NoData); // leading edge = NoData
    }

    [Fact]
    public void BuildCiTrend_quiet_hours_after_a_success_are_NoData()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var trend = DashboardRefreshService.BuildCiTrend(
            [RunAt(now - Duration.FromHours(5) - Duration.FromMinutes(10), "success")],
            now
        );
        _ = trend[CiTrendIndexForHoursAgo(5)].State.Should().Be(CiTrendState.Passing); // the hour the run landed
        _ = trend[MostRecentCiTrendIndex].State.Should().Be(CiTrendState.NoData); // quiet hours after = NoData
        _ = trend[0].State.Should().Be(CiTrendState.NoData); // leading edge = NoData
    }

    [Fact]
    public void BuildCiTrend_each_hour_reflects_only_its_own_runs()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var runs = new[]
        {
            RunAt(now - Duration.FromHours(20), "failure"), // 20h ago -> idx 4
            RunAt(now - Duration.FromHours(2), "success"), // 2h ago  -> idx 22
        };
        var trend = DashboardRefreshService.BuildCiTrend(runs, now);
        // Runs exactly on the hour bin into the hour they start: [now-20h, now-19h) = idx 4,
        // [now-2h, now-1h) = idx 22.
        _ = trend[4].State.Should().Be(CiTrendState.Failing); // failure hour
        _ = trend[5].State.Should().Be(CiTrendState.NoData); // quiet hour — not carried
        _ = trend[22].State.Should().Be(CiTrendState.Passing); // success hour
        _ = trend[23].State.Should().Be(CiTrendState.NoData); // quiet newest hour after success — not carried
        _ = trend[0].State.Should().Be(CiTrendState.NoData); // leading edge
    }

    [Fact]
    public void BuildCiTrend_bins_runs_by_clock_hour_stable_across_intra_hour_refreshes()
    {
        // A run at a fixed instant must land in the same bucket regardless of where
        // in the hour `now` falls — the whole point of hour-anchoring. Two refreshes
        // in the same clock hour (12:10 and 12:50) must agree on the run's bucket.
        var run = RunAt(Instant.FromUtc(2026, 5, 30, 11, 30), "failure");
        var early = DashboardRefreshService.BuildCiTrend([run], Instant.FromUtc(2026, 5, 30, 12, 10));
        var late = DashboardRefreshService.BuildCiTrend([run], Instant.FromUtc(2026, 5, 30, 12, 50));

        var earlyFailing = early
            .Select((b, i) => (b, i))
            .Where(x => x.b.State == CiTrendState.Failing)
            .Select(x => x.i);
        var lateFailing = late.Select((b, i) => (b, i)).Where(x => x.b.State == CiTrendState.Failing).Select(x => x.i);
        _ = earlyFailing.Should().Equal(lateFailing);
        // Both refreshes anchor to 13:00, so the 11:00 hour is bucket 22.
        _ = early[22].State.Should().Be(CiTrendState.Failing);
        _ = early[22].BucketStart.Should().Be(Instant.FromUtc(2026, 5, 30, 11, 0));
    }

    [Fact]
    public void BuildCiTrend_hours_without_runs_are_NoData()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var trend = DashboardRefreshService.BuildCiTrend([RunAt(now - Duration.FromMinutes(20), "success")], now);
        _ = trend[23].State.Should().Be(CiTrendState.Passing);
        _ = trend
            .Select((b, i) => new { b.State, i })
            .Should()
            .OnlyContain(x => x.i == 23 || x.State == CiTrendState.NoData);
    }

    [Fact]
    public void BuildCiTrend_excludes_runs_outside_the_24h_window()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var runs = new[]
        {
            RunAt(now + Duration.FromMinutes(5), "failure"), // future (clock skew)
        };
        _ = DashboardRefreshService.BuildCiTrend(runs, now).Should().OnlyContain(b => b.State == CiTrendState.NoData);
    }

    [Fact]
    public void BuildCiTrend_run_older_than_24h_produces_all_NoData()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var runs = new[] { RunAt(now - Duration.FromHours(25), "failure") };
        _ = DashboardRefreshService.BuildCiTrend(runs, now).Should().OnlyContain(b => b.State == CiTrendState.NoData);
    }

    [Fact]
    public void BuildCiTrendForRefresh_preserves_previous_bucket_history_when_refresh_is_degraded()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var previousTrend = DashboardRefreshService.BuildCiTrend(
            [RunAt(now - Duration.FromHours(2), "failure")],
            now - Duration.FromMinutes(1)
        );
        var previous = new DashboardSnapshot(
            now - Duration.FromMinutes(1),
            "FixPortal",
            [Repo("a", (SignalState.Failure, "failure"))],
            [],
            null,
            previousTrend
        );

        var refreshed = DashboardRefreshService.BuildCiTrendForRefresh(
            [
                (Repo("a"), true, []),
                (
                    Repo("b", (SignalState.Success, "success")),
                    false,
                    [RunAt(now - Duration.FromMinutes(20), "success")]
                ),
            ],
            now,
            previous
        );

        // Degraded repo 'a' has no fresh runs — prior Failing at idx 22 preserved via MergeTrends.
        // Non-degraded repo 'b' had a fresh success at now-20min — fresh Passing wins at idx 23.
        _ = refreshed[22].State.Should().Be(CiTrendState.Failing);
        _ = refreshed[23].State.Should().Be(CiTrendState.Passing);
        // Verify timestamps are updated to the fresh now, not frozen at previous RefreshedAt
        _ = refreshed[22].BucketStart.Should().Be(now - Duration.FromHours(2));
        _ = refreshed[23].BucketStart.Should().Be(now - Duration.FromHours(1));
    }

    [Fact]
    public void MergeTrends_aligns_buckets_by_clock_hour_across_a_nonzero_shift()
    {
        // Previous and fresh trends anchored to different hours (10:00 vs 12:00).
        // The previous Failing hour (06:00) must carry into whichever fresh bucket
        // shares that BucketStart, regardless of index offset.
        var previousNow = Instant.FromUtc(2026, 5, 30, 10, 0);
        var freshNow = Instant.FromUtc(2026, 5, 30, 12, 0);

        var previous = new List<CiTrendBucket>(24);
        for (var i = 0; i < 24; i++)
        {
            var time = previousNow - Duration.FromHours(24 - i);
            previous.Add(new CiTrendBucket(time, i == 20 ? CiTrendState.Failing : CiTrendState.NoData)); // 06:00 Failing
        }

        var fresh = new List<CiTrendBucket>(24);
        for (var i = 0; i < 24; i++)
        {
            var time = freshNow - Duration.FromHours(24 - i);
            fresh.Add(new CiTrendBucket(time, CiTrendState.NoData));
        }

        var merged = DashboardRefreshService.MergeTrends(previous, fresh);

        _ = merged[18].State.Should().Be(CiTrendState.Failing); // fresh 06:00 bucket inherits the prior Failing
        _ = merged[20].State.Should().Be(CiTrendState.NoData);
    }

    [Fact]
    public void BuildCiTrendForRefresh_preserves_previous_when_refresh_is_degraded_and_no_new_runs_exist()
    {
        var now = Instant.FromUtc(2026, 5, 30, 12, 0);
        var previousTrend = DashboardRefreshService.BuildCiTrend(
            [RunAt(now - Duration.FromHours(2), "failure")],
            now - Duration.FromMinutes(1)
        );
        var previous = new DashboardSnapshot(
            now - Duration.FromMinutes(1),
            "FixPortal",
            [Repo("a", (SignalState.Failure, "failure"))],
            [],
            null,
            previousTrend
        );

        var refreshed = DashboardRefreshService.BuildCiTrendForRefresh(
            [(Repo("a"), true, Array.Empty<WorkflowRun>())],
            now,
            previous
        );

        _ = refreshed.Select(b => b.State).Should().Equal(previousTrend.Select(b => b.State));
        _ = refreshed[0].BucketStart.Should().Be(now - Duration.FromHours(24));
        _ = refreshed[^1].BucketStart.Should().Be(now - Duration.FromHours(1));
    }

    [Fact]
    public async Task PersistAndPublishAsync_updates_live_state_even_when_persistence_fails()
    {
        var store = Substitute.For<IDashboardSnapshotStore>();
        _ = store
            .SaveAsync(Arg.Any<DashboardSnapshot>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk full"));
        var state = new DashboardSnapshotState();
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 30, 12, 0),
            "FixPortal",
            [Repo("a", (SignalState.Success, "success"))],
            [],
            null,
            []
        );

        await DashboardRefreshService.PersistAndPublishAsync(
            store,
            state,
            snapshot,
            snapshot,
            persist: true,
            logger: NullLogger<DashboardRefreshService>.Instance,
            CancellationToken.None
        );

        _ = state.Current.Should().BeSameAs(snapshot);
    }

    [Fact]
    public async Task PersistAndPublishAsync_propagates_cancellation_without_publishing_live_state()
    {
        var store = Substitute.For<IDashboardSnapshotStore>();
        var canceledToken = new CancellationToken(canceled: true);
        _ = store
            .SaveAsync(Arg.Any<DashboardSnapshot>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(canceledToken));
        var state = new DashboardSnapshotState();
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 30, 12, 0),
            "FixPortal",
            [Repo("a", (SignalState.Success, "success"))],
            [],
            null,
            []
        );

        var act = () =>
            DashboardRefreshService.PersistAndPublishAsync(
                store,
                state,
                snapshot,
                snapshot,
                persist: true,
                logger: NullLogger<DashboardRefreshService>.Instance,
                canceledToken
            );

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        _ = state.Current.Should().BeNull();
    }

    [Fact]
    public void InheritEnrichment_fills_cold_metrics_deploys_and_packages_from_previous_snapshot()
    {
        var priorMetrics = new RepoMetrics(1000, 2.0, 50, 1, Instant.MinValue);
        IReadOnlyList<JobSignal> priorDeploys = [new("CI", "Deploy", SignalState.Success, "u", Instant.MinValue)];
        IReadOnlyList<JobSignal> priorPackages = [new("CI", "Publish", SignalState.Success, "u", Instant.MinValue)];
        var previous = new DashboardSnapshot(
            Instant.MinValue,
            "FixPortal",
            [Repo("a") with { Metrics = priorMetrics, Deploys = priorDeploys, Packages = priorPackages }],
            [],
            null
        );

        // cold start: no metrics, empty deploys/packages
        var result = DashboardRefreshService.InheritEnrichment([Repo("a")], previous);

        _ = result[0].Metrics.Should().BeSameAs(priorMetrics);
        _ = result[0].Deploys.Should().BeSameAs(priorDeploys);
        _ = result[0].Packages.Should().BeSameAs(priorPackages);
    }

    [Fact]
    public void InheritEnrichment_does_not_overwrite_fresh_enrichment()
    {
        var freshMetrics = new RepoMetrics(500, 1.5, 20, 0, Instant.MinValue);
        IReadOnlyList<JobSignal> freshDeploys = [new("CI", "Deploy", SignalState.Failure, "u", Instant.MinValue)];
        var previous = new DashboardSnapshot(
            Instant.MinValue,
            "FixPortal",
            [
                Repo("a") with
                {
                    Metrics = new RepoMetrics(1000, 2.0, 50, 1, Instant.MinValue),
                    Deploys = [new JobSignal("CI", "Deploy", SignalState.Success, "u", Instant.MinValue)],
                },
            ],
            [],
            null
        );

        var result = DashboardRefreshService.InheritEnrichment(
            [Repo("a") with { Metrics = freshMetrics, Deploys = freshDeploys }],
            previous
        );

        _ = result[0].Metrics.Should().BeSameAs(freshMetrics);
        _ = result[0].Deploys.Should().BeSameAs(freshDeploys);
    }

    [Fact]
    public void InheritEnrichment_returns_current_unchanged_when_no_previous()
    {
        RepositorySnapshot[] current = [Repo("a"), Repo("b")];
        _ = DashboardRefreshService.InheritEnrichment(current, null).Should().BeSameAs(current);
    }

    [Fact]
    public void InheritEnrichment_applies_cached_merged_pr_when_no_previous()
    {
        var cached = new MergedPullRequest(1, "title", "u", "a", "author", Instant.FromUnixTimeSeconds(100));
        var mergedPrs = new PerRepoCache<MergedPullRequest>();
        mergedPrs.Update("a", cached);

        var result = DashboardRefreshService.InheritEnrichment([Repo("a")], null, mergedPrs);

        _ = result[0].LastMergedPr.Should().BeSameAs(cached);
    }

    [Fact]
    public void InheritEnrichment_prefers_cached_merged_pr_over_previous_snapshot()
    {
        var cached = new MergedPullRequest(99, "cached", "u", "a", "author", Instant.FromUnixTimeSeconds(500));
        var stale = new MergedPullRequest(1, "stale", "u", "a", "author", Instant.FromUnixTimeSeconds(100));
        var previous = new DashboardSnapshot(
            Instant.MinValue,
            "FixPortal",
            [Repo("a") with { LastMergedPr = stale }],
            [],
            null
        );
        var mergedPrs = new PerRepoCache<MergedPullRequest>();
        mergedPrs.Update("a", cached);

        var result = DashboardRefreshService.InheritEnrichment([Repo("a")], previous, mergedPrs);

        _ = result[0].LastMergedPr.Should().BeSameAs(cached);
    }

    // CB-H6: pure predicate guarding an all-failed cold start from persisting garbage.
    // Only the (anyFetchFailed: true, hasPrevious: false) combo must suppress persistence.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ShouldPersist_persists_unless_every_repo_failed_with_no_prior_snapshot(
        bool anyFetchFailed,
        bool hasPrevious,
        bool expected
    ) => _ = DashboardRefreshService.ShouldPersist(anyFetchFailed, hasPrevious).Should().Be(expected);
}
