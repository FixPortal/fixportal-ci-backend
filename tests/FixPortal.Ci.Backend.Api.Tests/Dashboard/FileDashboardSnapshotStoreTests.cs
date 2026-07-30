using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class FileDashboardSnapshotStoreTests
{
    [Fact]
    public async Task LoadAsync_should_return_null_when_snapshot_is_missing()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);

        try
        {
            var snapshot = await sut.LoadAsync(CancellationToken.None);

            _ = snapshot.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_round_trip_snapshot()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var snapshot = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 18, 0), "FixPortal", [], [], null);

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);

            // Record equality compares IReadOnlyList members by reference, so two
            // distinct but empty collections are never equal under a Be assertion,
            // whereas BeEquivalentTo compares structurally, which is what we want here.
            _ = reloaded.Should().BeEquivalentTo(snapshot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_round_trip_snapshot_with_populated_ci_trend()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var ciTrend = new[]
        {
            new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Passing),
            new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 17, 0), CiTrendState.Failing),
        };
        var snapshot = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 18, 0), "FixPortal", [], [], null, ciTrend);

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);
            _ = reloaded.Should().BeEquivalentTo(snapshot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_write_camel_case_enum_strings()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "repo",
                    "https://github.com/FixPortal/repo",
                    false,
                    [new WorkflowSnapshot("CI", "ci.yml", SignalState.Success, null)],
                    [],
                    null,
                    [],
                    []
                ),
            ],
            [],
            null,
            [new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Failing)]
        );

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var json = await File.ReadAllTextAsync(path, CancellationToken.None);

            _ = json.Should().Contain("\"state\":\"success\"");
            _ = json.Should().Contain("\"state\":\"failing\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_should_read_legacy_ordinal_fixture()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        const string legacySnapshot = """
            {"refreshedAt":"2026-05-28T18:00:00Z","org":"FixPortal","repositories":[{"name":"repo","htmlUrl":"https://github.com/FixPortal/repo","private":false,"workflows":[{"name":"CI","file":"ci.yml","state":0,"lastRun":null}],"pullRequests":[],"metrics":null,"deploys":[],"packages":[],"lastMergedPr":null}],"summary":[],"lastMergedPr":null,"ciTrend":[{"bucketStart":"2026-05-28T16:00:00Z","state":2,"isBackfilled":false}],"publicCiTrend":null}
            """;

        try
        {
            await File.WriteAllTextAsync(path, legacySnapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);

            _ = reloaded!.Repositories[0].Workflows[0].State.Should().Be(SignalState.Success);
            _ = reloaded.CiTrend![0].State.Should().Be(CiTrendState.Failing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_round_trip_the_persisted_public_ci_trend()
    {
        // B5-full: the public-only trend is persisted on the full snapshot so a
        // cold-start restore surfaces it accurately. It must survive the round trip.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var ciTrend = new[] { new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Failing) };
        var publicCiTrend = new[] { new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Passing) };
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [],
            [],
            null,
            ciTrend,
            publicCiTrend
        );

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);
            _ = reloaded!.PublicCiTrend.Should().BeEquivalentTo(publicCiTrend);
            _ = reloaded.Should().BeEquivalentTo(snapshot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_replace_the_existing_snapshot()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var firstSnapshot = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 18, 0), "Before refresh", [], [], null);
        var secondSnapshot = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 19, 0), "After refresh", [], [], null);

        try
        {
            await sut.SaveAsync(firstSnapshot, CancellationToken.None);
            await sut.SaveAsync(secondSnapshot, CancellationToken.None);

            var reloaded = await sut.LoadAsync(CancellationToken.None);

            _ = reloaded.Should().BeEquivalentTo(secondSnapshot);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_should_preserve_the_existing_snapshot_and_remove_temp_file_when_cancelled()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var initialSnapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "Before refresh",
            [],
            [],
            null
        );
        var replacementSnapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 19, 0),
            "After refresh",
            [],
            [],
            null
        );

        try
        {
            await sut.SaveAsync(initialSnapshot, CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            var save = () => sut.SaveAsync(replacementSnapshot, cancellation.Token);
            _ = await save.Should().ThrowAsync<OperationCanceledException>();

            var reloaded = await sut.LoadAsync(CancellationToken.None);
            _ = reloaded.Should().BeEquivalentTo(initialSnapshot);
            _ = File.Exists(path + ".tmp").Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task LoadAsync_should_discard_a_snapshot_with_non_hour_aligned_trend_buckets()
    {
        // A pre-anchoring snapshot has BucketStart at an arbitrary refresh instant
        // (e.g. 16:30), not a clock hour. MergeTrends keys on BucketStart, so such a
        // snapshot would silently drop history on a degraded first refresh — LoadAsync
        // must reject it and force a clean rebuild.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = new FileDashboardSnapshotStore(path);
        var staleTrend = new[] { new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 30), CiTrendState.Passing) };
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [],
            [],
            null,
            staleTrend
        );

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);
            _ = reloaded.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
