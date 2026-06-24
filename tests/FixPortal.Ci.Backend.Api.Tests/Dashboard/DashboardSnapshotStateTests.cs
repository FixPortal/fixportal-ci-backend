using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class DashboardSnapshotStateTests
{
    private static readonly Instant T = Instant.FromUtc(2026, 5, 28, 12, 0);

    private static RepositorySnapshot Repo(string name, bool isPrivate, SignalState wfState) =>
        new(name, $"https://github.com/FixPortal/{name}", isPrivate,
            [new WorkflowSnapshot("CI", "ci.yml", wfState, null)],
            [], null, [], []);

    private static DashboardSnapshot Snapshot(
        IReadOnlyList<RepositorySnapshot> repos, IReadOnlyList<CiTrendBucket>? trend) =>
        new(T, "FixPortal", repos, [], null, trend);

    [Fact]
    public void ComputePublicSnapshot_never_surfaces_a_Failing_bucket_on_cold_start()
    {
        // The persisted full trend mixes public and private repos and carries no
        // per-repo run history, so a Failing bucket cannot be attributed. The public
        // cold-start trend must therefore never show Failing — even when a public
        // repo is currently failing — or a private repo's failure would leak.
        var full = Snapshot(
            [Repo("priv", isPrivate: true, SignalState.Failure),
             Repo("pub", isPrivate: false, SignalState.Failure)],
            [
                new CiTrendBucket(T, CiTrendState.Failing),
                new CiTrendBucket(T.Plus(Duration.FromHours(1)), CiTrendState.Passing),
                new CiTrendBucket(T.Plus(Duration.FromHours(2)), CiTrendState.NoData),
            ]);

        var pub = DashboardSnapshotState.ComputePublicSnapshot(full);

        _ = pub.CiTrend!.Should().NotContain(b => b.State == CiTrendState.Failing);
        _ = pub.CiTrend!.Select(b => b.State).Should().Equal(
            CiTrendState.Passing, CiTrendState.Passing, CiTrendState.NoData);
    }

    [Fact]
    public void ComputePublicSnapshot_renders_all_NoData_when_no_public_repos()
    {
        var full = Snapshot(
            [Repo("priv", isPrivate: true, SignalState.Failure)],
            [new CiTrendBucket(T, CiTrendState.Failing), new CiTrendBucket(T.Plus(Duration.FromHours(1)), CiTrendState.Passing)]);

        var pub = DashboardSnapshotState.ComputePublicSnapshot(full);

        _ = pub.Repositories.Should().BeEmpty();
        _ = pub.CiTrend!.Should().OnlyContain(b => b.State == CiTrendState.NoData);
    }

    [Fact]
    public void Update_publishes_current_and_public_together()
    {
        var state = new DashboardSnapshotState();
        var current = Snapshot([Repo("priv", true, SignalState.Success)], null);
        var publicSnap = Snapshot([], null);

        state.Update(current, publicSnap);

        _ = state.Current.Should().BeSameAs(current);
        _ = state.Public.Should().BeSameAs(publicSnap);
    }
}
