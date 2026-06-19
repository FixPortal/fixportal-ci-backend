using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class PerRepoCacheTests
{
    [Fact]
    public void TryGet_returns_false_and_default_for_unknown_repo()
    {
        _ = new PerRepoCache<RepoMetrics>().TryGet("nope", out var metrics).Should().BeFalse();
        _ = metrics.Should().BeNull();
    }

    [Fact]
    public void Update_then_TryGet_returns_latest_metrics()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var m = new RepoMetrics(100, 1.5, 10, 0, Instant.FromUnixTimeSeconds(5));
        cache.Update("repo", m);
        _ = cache.TryGet("repo", out var got).Should().BeTrue();
        _ = got.Should().Be(m);
    }

    [Fact]
    public void Update_then_TryGet_returns_latest_deploys()
    {
        var cache = new PerRepoCache<IReadOnlyList<JobSignal>>();
        var d = new JobSignal("CI", "Deploy (prod)", SignalState.Success, "u", Instant.FromUnixTimeSeconds(5));
        cache.Update("repo", [d]);
        _ = cache.TryGet("repo", out var got).Should().BeTrue();
        _ = got.Should().ContainSingle().Which.Should().Be(d);
    }

    [Fact]
    public void Update_is_case_insensitive_on_repo_key()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var m = new RepoMetrics(1, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        cache.Update("Repo", m);
        _ = cache.TryGet("repo", out var got).Should().BeTrue();
        _ = got.Should().Be(m);
    }

    [Fact]
    public void TryGet_returns_value_when_entry_within_ttl()
    {
        var clock = new FakeClock(Instant.FromUnixTimeSeconds(1000));
        var cache = new PerRepoCache<RepoMetrics>(clock, Duration.FromMinutes(10));
        var m = new RepoMetrics(1, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        cache.Update("repo", m);

        clock.AdvanceMinutes(9);

        _ = cache.TryGet("repo", out var got).Should().BeTrue();
        _ = got.Should().Be(m);
    }

    [Fact]
    public void TryGet_returns_false_when_entry_exceeds_ttl()
    {
        var clock = new FakeClock(Instant.FromUnixTimeSeconds(1000));
        var cache = new PerRepoCache<RepoMetrics>(clock, Duration.FromMinutes(10));
        var m = new RepoMetrics(1, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        cache.Update("repo", m);

        clock.AdvanceMinutes(11);

        _ = cache.TryGet("repo", out var got).Should().BeFalse();
        _ = got.Should().BeNull();
    }

    [Fact]
    public void TryGet_returns_value_after_refresh_clears_expired_entry()
    {
        var clock = new FakeClock(Instant.FromUnixTimeSeconds(1000));
        var cache = new PerRepoCache<RepoMetrics>(clock, Duration.FromMinutes(10));
        var stale = new RepoMetrics(1, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        cache.Update("repo", stale);
        clock.AdvanceMinutes(11);

        var fresh = new RepoMetrics(2, 2.0, 2, 0, Instant.FromUnixTimeSeconds(2));
        cache.Update("repo", fresh);

        _ = cache.TryGet("repo", out var got).Should().BeTrue();
        _ = got.Should().Be(fresh);
    }

    [Fact]
    public void TryGet_returns_false_and_default_for_unknown_repo_with_ttl_configured()
    {
        var clock = new FakeClock(Instant.FromUnixTimeSeconds(1000));
        var cache = new PerRepoCache<RepoMetrics>(clock, Duration.FromMinutes(10));
        _ = cache.TryGet("nope", out var metrics).Should().BeFalse();
        _ = metrics.Should().BeNull();
    }

    [Fact]
    public void Update_multiple_times_with_different_casing_overwrites_same_entry()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var m1 = new RepoMetrics(1, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        var m2 = new RepoMetrics(2, 2.0, 2, 0, Instant.FromUnixTimeSeconds(2));
        var m3 = new RepoMetrics(3, 3.0, 3, 0, Instant.FromUnixTimeSeconds(3));
        cache.Update("Repo", m1);
        cache.Update("REPO", m2);
        cache.Update("repo", m3);
        _ = cache.TryGet("Repo", out var gotRepo).Should().BeTrue();
        _ = gotRepo.Should().Be(m3);
        _ = cache.TryGet("REPO", out var gotUpper).Should().BeTrue();
        _ = gotUpper.Should().Be(m3);
        _ = cache.TryGet("repo", out var gotLower).Should().BeTrue();
        _ = gotLower.Should().Be(m3);
    }
}
