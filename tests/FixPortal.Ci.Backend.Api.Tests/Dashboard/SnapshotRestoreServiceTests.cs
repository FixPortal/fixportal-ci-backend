using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// CB-C3: SnapshotRestoreService.StartingAsync runs before Kestrel accepts traffic
// and is otherwise never constructed by any test (the WAF endpoint tests
// RemoveAll<IHostedService>() and seed DashboardSnapshotState directly instead).
public class SnapshotRestoreServiceTests
{
    private static DashboardSnapshot SnapshotWithPrivateRepo() =>
        new(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "public-repo",
                    "https://github.com/FixPortal/public-repo",
                    false,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "private-repo",
                    "https://github.com/FixPortal/private-repo",
                    true,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
            ],
            [],
            null
        );

    private static DashboardSnapshot SnapshotWithHeadScopedReviewState()
    {
        var snapshot = SnapshotWithPrivateRepo();
        var publicRepo = snapshot.Repositories[0] with
        {
            PullRequests =
            [
                new PullRequest(
                    181,
                    "Review-derived state must not survive restore",
                    "author",
                    "https://github.com/FixPortal/public-repo/pull/181",
                    false,
                    Instant.FromUtc(2026, 6, 1, 0, 0),
                    [new ReviewSignal("Gitar", ReviewSignalState.Clean, null, null)]
                )
                {
                    ReadyToMerge = true,
                },
            ],
        };
        return snapshot with { Repositories = [publicRepo, snapshot.Repositories[1]] };
    }

    [Fact]
    public async Task StartingAsync_should_re_derive_the_public_view_and_exclude_private_repos()
    {
        // Real store over a temp file: a stale persisted "public" field must never be
        // trusted (the point of ComputePublicSnapshot re-derivation) — a private-repo
        // leak could ride along a file across a code change, so this must be computed
        // fresh from the persisted full snapshot, not read verbatim from disk.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileDashboardSnapshotStore(path);
        var snapshot = SnapshotWithPrivateRepo();
        await store.SaveAsync(snapshot, TestContext.Current.CancellationToken);
        try
        {
            var state = new DashboardSnapshotState();
            var sut = new SnapshotRestoreService(store, state, NullLogger<SnapshotRestoreService>.Instance);

            await sut.StartingAsync(TestContext.Current.CancellationToken);

            _ = state.Current.Should().NotBeNull();
            _ = state.Current!.Repositories.Should().HaveCount(2);
            _ = state.Public.Should().NotBeNull();
            _ = state.Public!.Repositories.Should().ContainSingle(r => r.Name == "public-repo");
            _ = state.Public.Repositories.Should().NotContain(r => r.Name == "private-repo");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StartingAsync_should_strip_head_scoped_review_state_from_a_persisted_snapshot()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var store = new FileDashboardSnapshotStore(path);
        await store.SaveAsync(SnapshotWithHeadScopedReviewState(), TestContext.Current.CancellationToken);
        try
        {
            var state = new DashboardSnapshotState();
            var sut = new SnapshotRestoreService(store, state, NullLogger<SnapshotRestoreService>.Instance);

            await sut.StartingAsync(TestContext.Current.CancellationToken);

            var pullRequest = state.Current!.Repositories[0].PullRequests.Should().ContainSingle().Which;
            _ = pullRequest.ReviewSignals.Should().BeNull();
            _ = pullRequest.ReadyToMerge.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StartingAsync_should_swallow_a_non_cancellation_load_failure_and_start_with_an_empty_board()
    {
        // A corrupt persisted file must not abort host startup: the guard degrades to
        // an empty board rather than crashing. If the swallow is ever removed, this
        // test starts throwing.
        var store = Substitute.For<IDashboardSnapshotStore>();
        _ = store.LoadAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new JsonException("corrupt snapshot file"));
        var state = new DashboardSnapshotState();
        var sut = new SnapshotRestoreService(store, state, NullLogger<SnapshotRestoreService>.Instance);

        var act = async () => await sut.StartingAsync(TestContext.Current.CancellationToken);

        _ = await act.Should().NotThrowAsync();
        _ = state.Current.Should().BeNull();
        _ = state.Public.Should().BeNull();
    }

    [Fact]
    public async Task StartingAsync_should_rethrow_on_host_shutdown_cancellation_rather_than_swallow_it()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelledToken = cts.Token;
        var store = Substitute.For<IDashboardSnapshotStore>();
        _ = store
            .LoadAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(_ => new OperationCanceledException(cancelledToken));
        var state = new DashboardSnapshotState();
        var sut = new SnapshotRestoreService(store, state, NullLogger<SnapshotRestoreService>.Instance);

        var act = async () => await sut.StartingAsync(cancelledToken);

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
