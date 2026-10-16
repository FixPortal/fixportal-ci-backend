using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class FileDashboardSnapshotStoreTests
{
    // The fingerprint decides whether a persisted snapshot may be restored, so a test that
    // is not about filter identity supplies a constant one: save and load then agree, and
    // the behaviour under test is whatever else the case is asserting.
    private const string SameFilters = "same-filters";

    private static FileDashboardSnapshotStore NewStore(string path, string fingerprint = SameFilters) =>
        new(path, fingerprint, NullLogger<FileDashboardSnapshotStore>.Instance);

    [Fact]
    public async Task LoadAsync_should_return_null_when_snapshot_is_missing()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);

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
        var sut = NewStore(path);
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
        var sut = NewStore(path);
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

    // C4 (test audit 2026-10-11): every round trip above uses empty repository lists, so a nested
    // property dropped by the serializer (an ignore attribute, a converter that loses a member)
    // reloads as its default and the structural comparison cannot see it. Every member here is set
    // to a non-default value, so a silent drop shows. Compared on the raw LoadAsync result, not
    // through SnapshotRestoreService, which strips head-scoped signals.
    [Fact]
    public async Task SaveAsync_should_round_trip_a_fully_populated_snapshot()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);
        var at = Instant.FromUtc(2026, 5, 28, 17, 30);
        var run = new WorkflowRun(
            "completed",
            "failure",
            "https://github.com/FixPortal/repo/actions/runs/9",
            "Build the thing",
            42,
            "feature/x",
            "pull_request",
            at,
            "repo",
            "ci.yml",
            9_000_000_001L,
            3,
            "abc123def456"
        );
        var merged = new MergedPullRequest(
            7,
            "Merged one",
            "alice",
            "repo",
            "https://github.com/FixPortal/repo/pull/7",
            at
        );
        var repository = new RepositorySnapshot(
            "repo",
            "https://github.com/FixPortal/repo",
            true,
            [new WorkflowSnapshot("CI", "ci.yml", SignalState.Failure, run, [run, run with { RunNumber = 41 }])],
            [
                new PullRequest(
                    12,
                    "Add a thing",
                    "bob",
                    "https://github.com/FixPortal/repo/pull/12",
                    true,
                    at,
                    [new ReviewSignal("Gitar", ReviewSignalState.Outstanding, 3, "https://example.test/gitar")],
                    true,
                    "feedface"
                ),
            ],
            new RepoMetrics(1234, 4.5, 80, 6, at),
            [new JobSignal("deploy.yml", "deploy", SignalState.Running, "https://example.test/deploy", at)],
            [new JobSignal("publish.yml", "publish", SignalState.Success, "https://example.test/publish", at)],
            merged
        );
        var snapshot = new DashboardSnapshot(
            at,
            "FixPortal",
            [repository],
            [new SummaryCount("failing", 5)],
            merged,
            [new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 17, 0), CiTrendState.Failing) { IsBackfilled = true }],
            [new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Passing)]
        );

        try
        {
            await sut.SaveAsync(snapshot, CancellationToken.None);
            var reloaded = await sut.LoadAsync(CancellationToken.None);

            _ = reloaded.Should().BeEquivalentTo(snapshot, options => options.WithStrictOrdering());
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
        var sut = NewStore(path);
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
        // Subject is the ORDINAL enum encoding ("state":0 / "state":2) written by older
        // versions, which must still parse to the right members. The body is verbatim what
        // those versions wrote; only the envelope around it is current.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);
        const string legacyBody = """
            {"refreshedAt":"2026-05-28T18:00:00Z","org":"FixPortal","repositories":[{"name":"repo","htmlUrl":"https://github.com/FixPortal/repo","private":false,"workflows":[{"name":"CI","file":"ci.yml","state":0,"lastRun":null}],"pullRequests":[],"metrics":null,"deploys":[],"packages":[],"lastMergedPr":null}],"summary":[],"lastMergedPr":null,"ciTrend":[{"bucketStart":"2026-05-28T16:00:00Z","state":2,"isBackfilled":false}],"publicCiTrend":null}
            """;
        var enveloped = $$"""{"filterFingerprint":"{{SameFilters}}","snapshot":{{legacyBody}}}""";

        try
        {
            await File.WriteAllTextAsync(path, enveloped, CancellationToken.None);
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
    public async Task LoadAsync_should_discard_a_pre_envelope_snapshot_whose_filter_provenance_is_unknown()
    {
        // A file written before the fingerprint envelope carries no record of which filters
        // produced it, so it cannot be shown to match the current ones. Discarding costs one
        // empty board and one rebuild of trend history at the deploy that ships this;
        // restoring it would serve a repository set nobody can vouch for, publicly.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);
        const string bareSnapshot = """
            {"refreshedAt":"2026-05-28T18:00:00Z","org":"FixPortal","repositories":[],"summary":[],"lastMergedPr":null}
            """;

        try
        {
            await File.WriteAllTextAsync(path, bareSnapshot, CancellationToken.None);
            _ = (await sut.LoadAsync(CancellationToken.None)).Should().BeNull();
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
        var sut = NewStore(path);
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
        var sut = NewStore(path);
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
    public async Task SaveAsync_should_preserve_committed_data_and_recover_after_serialization_fails()
    {
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = ceiling.Token;
        await OwnedSnapshotDirectory.RunAsync(async fixture =>
        {
            var store = NewStore(fixture.SnapshotPath);
            var previous = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 18, 0), "Previous", [], [], null);
            await store.SaveAsync(previous, ct);
            var committed = await File.ReadAllBytesAsync(fixture.SnapshotPath, ct);
            var invalid = previous with
            {
                Org = "Replacement",
                Repositories =
                [
                    new RepositorySnapshot(
                        "replacement",
                        "https://example.test/replacement",
                        false,
                        [],
                        [],
                        new RepoMetrics(2, double.NaN, 3, 1, previous.RefreshedAt),
                        null,
                        null
                    ),
                ],
            };

            // Production JSON rejects the non-finite metric after opening the temp file.
            // This exercises serialization failure, not cancellation or a flush/fsync fault.
            var save = () => store.SaveAsync(invalid, ct);
            _ = await save.Should().ThrowAsync<ArgumentException>();

            _ = (await File.ReadAllBytesAsync(fixture.SnapshotPath, ct)).Should().Equal(committed);
            _ = (await store.LoadAsync(ct)).Should().BeEquivalentTo(previous);
            _ = File.Exists(fixture.SnapshotPath + ".tmp").Should().BeFalse();

            var recovery = previous with
            {
                Org = "Recovered",
                RefreshedAt = previous.RefreshedAt + Duration.FromHours(1),
            };
            await store.SaveAsync(recovery, ct);
            _ = (await store.LoadAsync(ct)).Should().BeEquivalentTo(recovery);
            _ = File.Exists(fixture.SnapshotPath + ".tmp").Should().BeFalse();
        });
    }

    [Fact]
    public async Task SaveAsync_should_preserve_the_existing_snapshot_and_remove_temp_file_when_cancelled()
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);
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

            var cancellationToken = cancellation.Token;
            var save = () => sut.SaveAsync(replacementSnapshot, cancellationToken);
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
        var sut = NewStore(path);
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

    // Split deliberately. A single case placing the same misaligned bucket in BOTH trends is
    // satisfied by the CiTrend check alone, so the PublicCiTrend clause could be deleted and
    // the test would stay green. Each theory case now misaligns exactly one trend, so each
    // clause has a case that fails without it.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LoadAsync_should_discard_a_snapshot_misaligned_in_either_trend_alone(
        bool misalignCiTrend,
        bool misalignPublicTrend
    )
    {
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var sut = NewStore(path);
        var aligned = new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 16, 0), CiTrendState.Passing);
        // Not the first bucket: the guard must inspect every bucket, not just index 0.
        var misaligned = new CiTrendBucket(Instant.FromUtc(2026, 5, 28, 17, 30), CiTrendState.Passing);
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [],
            [],
            null,
            misalignCiTrend ? [aligned, misaligned] : [aligned],
            misalignPublicTrend ? [aligned, misaligned] : [aligned]
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

    [Fact]
    public async Task LoadAsync_should_discard_a_snapshot_written_under_different_repository_filters()
    {
        // The defect this closes: restore republishes the repository set the snapshot was
        // written with, so tightening a filter and restarting serves the OLD, wider set --
        // to the anonymous public projection too, and with no time bound while GitHub is
        // unreachable.
        var path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        var writer = NewStore(path, "filters-before");
        var reader = NewStore(path, "filters-after");
        var snapshot = new DashboardSnapshot(Instant.FromUtc(2026, 5, 28, 18, 0), "FixPortal", [], [], null);

        try
        {
            await writer.SaveAsync(snapshot, CancellationToken.None);

            _ = (await reader.LoadAsync(CancellationToken.None)).Should().BeNull();
            // Same fingerprint still restores: the guard must not cost a restore on every boot.
            _ = (await writer.LoadAsync(CancellationToken.None)).Should().NotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FilterFingerprint_should_ignore_pattern_order_but_track_pattern_changes()
    {
        var baseline = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["legacy-*", "spike-*"],
        };
        var reordered = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["spike-*", "legacy-*"],
        };
        var changed = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["legacy-*"],
        };

        // Reordering an include list is not a semantic change and must not discard history.
        _ = reordered.FilterFingerprint().Should().Be(baseline.FilterFingerprint());
        _ = changed.FilterFingerprint().Should().NotBe(baseline.FilterFingerprint());
    }

    [Fact]
    public void SnapshotFingerprint_should_track_owner_case_insensitively()
    {
        var options = new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 };

        _ = options.SnapshotFingerprint("FixPortal").Should().Be(options.SnapshotFingerprint("fixportal"));
        _ = options.SnapshotFingerprint("OtherOrg").Should().NotBe(options.SnapshotFingerprint("FixPortal"));
    }

    [Theory]
    // A delimiter-joined encoding collides whenever a pattern can contain the delimiter:
    // one pattern holding the separator encodes identically to two patterns split on it.
    // A collision means a snapshot written under DIFFERENT filters passes the provenance
    // check, which is the exact failure the fingerprint exists to prevent.
    [InlineData("ab")]
    [InlineData("a;b")]
    [InlineData("a|b")]
    [InlineData("2:ab")]
    public void FilterFingerprint_should_not_collide_when_a_pattern_contains_a_separator(string joined)
    {
        var parts = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["a", "b"],
        };
        var single = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = [joined],
        };

        _ = single.FilterFingerprint().Should().NotBe(parts.FilterFingerprint());
    }

    [Fact]
    public void FilterFingerprint_should_not_collide_on_isolated_surrogates()
    {
        // Encoding.UTF8.GetBytes substitutes U+FFFD for an isolated surrogate, so every
        // lone-surrogate pattern hashed to the same bytes. Nothing in the validators
        // rejects one, and the consequence is the same as any other collision: a snapshot
        // written under different filters passes the provenance check.
        var high = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["\uD800"],
        };
        var other = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["\uD801"],
        };

        _ = other.FilterFingerprint().Should().NotBe(high.FilterFingerprint());
    }

    [Fact]
    public void FilterFingerprint_should_not_collide_across_different_filter_kinds()
    {
        // The same pattern in a different list is a different configuration: excluding
        // "legacy-*" is not including it.
        var excluded = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            ExcludeRepositories = ["legacy-*"],
        };
        var included = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 20,
            IncludeRepositories = ["legacy-*"],
        };

        _ = included.FilterFingerprint().Should().NotBe(excluded.FilterFingerprint());
    }
}
