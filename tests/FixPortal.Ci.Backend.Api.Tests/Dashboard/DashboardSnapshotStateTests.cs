using System.Collections;
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
        new(
            name,
            $"https://github.com/FixPortal/{name}",
            isPrivate,
            [new WorkflowSnapshot("CI", "ci.yml", wfState, null)],
            [],
            null,
            [],
            []
        );

    private static DashboardSnapshot Snapshot(
        IReadOnlyList<RepositorySnapshot> repos,
        IReadOnlyList<CiTrendBucket>? trend
    ) => new(T, "FixPortal", repos, [], null, trend);

    private static DashboardSnapshot SnapshotWithPullRequest(string headSha, bool readyToMerge) =>
        Snapshot(
            [
                Repo("repo", false, SignalState.Success) with
                {
                    PullRequests =
                    [
                        new PullRequest(
                            42,
                            "PR",
                            "alice",
                            "https://github.com/FixPortal/repo/pull/42",
                            false,
                            T,
                            ReadyToMerge: readyToMerge,
                            HeadSha: headSha
                        ),
                    ],
                },
            ],
            null
        );

    [Fact]
    public void ComputePublicSnapshot_never_surfaces_a_Failing_bucket_on_cold_start()
    {
        // The persisted full trend mixes public and private repos and carries no
        // per-repo run history, so a Failing bucket cannot be attributed. The public
        // cold-start trend must therefore never show Failing — even when a public
        // repo is currently failing — or a private repo's failure would leak.
        var full = Snapshot(
            [Repo("priv", isPrivate: true, SignalState.Failure), Repo("pub", isPrivate: false, SignalState.Failure)],
            [
                new CiTrendBucket(T, CiTrendState.Failing),
                new CiTrendBucket(T.Plus(Duration.FromHours(1)), CiTrendState.Passing),
                new CiTrendBucket(T.Plus(Duration.FromHours(2)), CiTrendState.NoData),
            ]
        );

        var pub = DashboardSnapshotState.ComputePublicSnapshot(full);

        _ = pub.CiTrend!.Should().NotContain(b => b.State == CiTrendState.Failing);
        _ = pub.CiTrend!.Select(b => b.State)
            .Should()
            .Equal(CiTrendState.NoData, CiTrendState.Passing, CiTrendState.NoData);
    }

    [Fact]
    public void ComputePublicSnapshot_renders_all_NoData_when_no_public_repos()
    {
        var full = Snapshot(
            [Repo("priv", isPrivate: true, SignalState.Failure)],
            [
                new CiTrendBucket(T, CiTrendState.Failing),
                new CiTrendBucket(T.Plus(Duration.FromHours(1)), CiTrendState.Passing),
            ]
        );

        var pub = DashboardSnapshotState.ComputePublicSnapshot(full);

        _ = pub.Repositories.Should().BeEmpty();
        _ = pub.CiTrend!.Should().OnlyContain(b => b.State == CiTrendState.NoData);
    }

    [Fact]
    public void ComputePublicSnapshot_uses_the_persisted_public_trend_verbatim()
    {
        // B5-full: when a persisted public-only trend is supplied (cold-start restore
        // from a snapshot that carried PublicCiTrend), it is used as-is — it was
        // computed from public repos only, so a genuine public Failing is accurate
        // and must survive, unlike the lossy fallback which reclassifies every Failing.
        var full = Snapshot(
            [Repo("pub", isPrivate: false, SignalState.Failure)],
            [new CiTrendBucket(T, CiTrendState.Passing)]
        );
        var persistedPublic = new[]
        {
            new CiTrendBucket(T, CiTrendState.Failing),
            new CiTrendBucket(T.Plus(Duration.FromHours(1)), CiTrendState.Passing),
        };

        var pub = DashboardSnapshotState.ComputePublicSnapshot(full, persistedPublic);

        _ = pub.CiTrend.Should().BeSameAs(persistedPublic);
        _ = pub.CiTrend!.Select(b => b.State).Should().Equal(CiTrendState.Failing, CiTrendState.Passing);
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

    [Fact]
    public async Task MarkNotMergeable_does_not_overwrite_a_concurrent_snapshot_update()
    {
        var state = new DashboardSnapshotState();
        var repositories = new BlockingReadOnlyList<RepositorySnapshot>([Repo("old", false, SignalState.Success)]);
        state.Update(Snapshot(repositories, null), Snapshot([], null));

        var patch = Task.Run(() => state.MarkNotMergeable("old", 42, "head-a"), TestContext.Current.CancellationToken);
        await repositories.EnumerationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var fresh = Snapshot([Repo("fresh", false, SignalState.Success)], null);
        var update = Task.Run(() => state.Update(fresh, Snapshot([], null)), TestContext.Current.CancellationToken);
        _ = await Task.WhenAny(
            update,
            Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken)
        );
        repositories.AllowEnumeration.TrySetResult();
        await Task.WhenAll(patch, update);

        _ = state.Current.Should().BeSameAs(fresh);
    }

    [Theory]
    [InlineData("head-a", true)]
    [InlineData("head-b", false)]
    public void MarkNotMergeable_only_patches_the_head_that_was_rejected(string rejectedHead, bool expectedReady)
    {
        var state = new DashboardSnapshotState();
        var snapshot = SnapshotWithPullRequest("head-b", readyToMerge: true);
        state.Update(snapshot, snapshot);

        state.MarkNotMergeable("repo", 42, rejectedHead);

        _ = state.Current!.Repositories.Single().PullRequests.Single().ReadyToMerge.Should().Be(expectedReady);
    }

    private sealed class BlockingReadOnlyList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public TaskCompletionSource EnumerationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEnumeration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => inner.Count;
        public T this[int index] => inner[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationStarted.TrySetResult();
            AllowEnumeration.Task.GetAwaiter().GetResult();
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
