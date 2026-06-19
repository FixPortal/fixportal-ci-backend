using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class RepoEnrichmentWorkerTests
{
    private static GitHubRepoDto Repo(string name) => new(name, $"https://github.com/FixPortal/{name}", false, false, "main");

    // Fake subclass over an in-memory cache. Passes client/inventory: null! —
    // RunSweepAsync never touches them (it receives the repo list directly).
    private sealed class FakeEnrichmentWorker(
        PerRepoCache<RepoMetrics> cache, Func<GitHubRepoDto, RepoMetrics?> collect, bool enabled = true)
        : RepoEnrichmentWorker<RepoMetrics>(null!, null!, cache, NullLogger.Instance)
    {
        protected override bool Enabled => enabled;
        protected override TimeSpan Cadence => TimeSpan.FromMilliseconds(1);
        protected override string Name => "Fake";
        protected override Task<RepoMetrics?> CollectAsync(GitHubRepoDto repo, CancellationToken ct)
            => Task.FromResult(collect(repo));

        public Task Sweep(IReadOnlyList<GitHubRepoDto> repos, CancellationToken ct) => RunSweepAsync(repos, ct);
    }

    [Fact]
    public async Task RunSweep_writes_value_when_collect_returns_nonnull()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var m = new RepoMetrics(10, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        var worker = new FakeEnrichmentWorker(cache, _ => m);

        await worker.Sweep([Repo("a")], CancellationToken.None);

        _ = cache.TryGet("a", out var got).Should().BeTrue();
        _ = got.Should().Be(m);
    }

    [Fact]
    public async Task RunSweep_keeps_prior_when_collect_returns_null()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var prior = new RepoMetrics(99, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        cache.Update("a", prior);
        var worker = new FakeEnrichmentWorker(cache, _ => null);

        await worker.Sweep([Repo("a")], CancellationToken.None);

        _ = cache.TryGet("a", out var got).Should().BeTrue();
        _ = got.Should().Be(prior);
    }

    [Fact]
    public async Task Disabled_worker_returns_without_sweeping()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var worker = new FakeEnrichmentWorker(cache, _ => throw new InvalidOperationException("collect must not run"), enabled: false);

        await worker.StartAsync(CancellationToken.None);
        // Disabled => ExecuteAsync returns immediately, so its task completes promptly.
        // Await it (rather than asserting synchronous completion, which the framework
        // does not guarantee) and confirm collect never ran — the cache stays empty.
        await worker.ExecuteTask!;

        _ = worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _ = cache.TryGet("anything", out _).Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }
}
