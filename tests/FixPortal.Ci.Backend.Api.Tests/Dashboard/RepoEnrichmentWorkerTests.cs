using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class RepoEnrichmentWorkerTests
{
    private static GitHubRepoDto Repo(string name) =>
        new(name, $"https://github.com/FixPortal/{name}", false, false, "main");

    // Fake subclass over an in-memory cache. Passes client/inventory: null! —
    // RunSweepAsync never touches them (it receives the repo list directly).
    private sealed class FakeEnrichmentWorker(
        PerRepoCache<RepoMetrics> cache,
        Func<GitHubRepoDto, RepoMetrics?> collect,
        bool enabled = true
    ) : RepoEnrichmentWorker<RepoMetrics>(null!, null!, cache, TimeProvider.System, NullLogger.Instance)
    {
        protected override bool Enabled => enabled;
        protected override TimeSpan Cadence => TimeSpan.FromMilliseconds(1);
        protected override string Name => "Fake";

        protected override Task<RepoMetrics?> CollectAsync(GitHubRepoDto repo, CancellationToken ct) =>
            Task.FromResult(collect(repo));

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
    public async Task RunSweep_continues_past_a_per_repo_auth_failure()
    {
        // A GitHubAuthException on one repo (e.g. the PAT lacks access to it) must
        // skip only that repo — the sweep must still enrich the others, not abort
        // and leave every repo's signal stale for the cycle.
        var cache = new PerRepoCache<RepoMetrics>();
        var value = new RepoMetrics(10, 1.0, 1, 0, Instant.FromUnixTimeSeconds(1));
        var worker = new FakeEnrichmentWorker(
            cache,
            repo => repo.Name == "denied" ? throw new GitHubAuthException("403") : value
        );

        await worker.Sweep([Repo("a"), Repo("denied"), Repo("c")], CancellationToken.None);

        _ = cache.TryGet("a", out _).Should().BeTrue();
        _ = cache.TryGet("c", out _).Should().BeTrue();
        _ = cache.TryGet("denied", out _).Should().BeFalse();
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
        var worker = new FakeEnrichmentWorker(
            cache,
            _ => throw new InvalidOperationException("collect must not run"),
            enabled: false
        );

        await worker.StartAsync(CancellationToken.None);
        // Disabled => ExecuteAsync returns immediately, so its task completes promptly.
        // Await it (rather than asserting synchronous completion, which the framework
        // does not guarantee) and confirm collect never ran — the cache stays empty.
        await worker.ExecuteTask!;

        _ = worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _ = cache.TryGet("anything", out _).Should().BeFalse();
        await worker.StopAsync(CancellationToken.None);
    }

    // Handler backing a real GitHubInventoryCache for the ExecuteAsync-driving test
    // below: ExecuteAsync's cold-start loop calls Inventory.GetRepositoriesAsync
    // itself (unlike RunSweepAsync, which receives the repo list directly), so a
    // real inventory over a fake HTTP handler is needed rather than null!.
    private sealed class SingleRepoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"name":"a","html_url":"https://github.com/FixPortal/a","private":false,"archived":false,"default_branch":"main"}]""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
    }

    private static GitHubInventoryCache NewSingleRepoInventory()
    {
        var http = new HttpClient(new SingleRepoHandler()) { BaseAddress = new Uri("https://api.github.com/") };
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        return new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions);
    }

    private sealed class ExecuteAsyncFakeWorker(
        GitHubInventoryCache inventory,
        PerRepoCache<RepoMetrics> cache,
        TimeProvider timeProvider,
        Func<GitHubRepoDto, RepoMetrics?> collect
    ) : RepoEnrichmentWorker<RepoMetrics>(null!, inventory, cache, timeProvider, NullLogger.Instance)
    {
        protected override bool Enabled => true;
        protected override TimeSpan Cadence => TimeSpan.FromMilliseconds(1);
        protected override string Name => "FakeExecuteAsync";

        protected override Task<RepoMetrics?> CollectAsync(GitHubRepoDto repo, CancellationToken ct) =>
            Task.FromResult(collect(repo));
    }

    // TrackingFakeTimeProvider lives in TrackingFakeTimeProvider.cs (shared with
    // ReviewSignalMergeTests) rather than nested here.

    // CB-H8: drives the base class's real ExecuteAsync loop (not RunSweepAsync
    // directly) so the cold-start retry guard itself is under test. On a failed
    // cold-start sweep, ExecuteAsync must wait five minutes before trying again.
    [Fact]
    public async Task ExecuteAsync_should_not_hot_loop_after_a_failed_cold_start_sweep()
    {
        var cache = new PerRepoCache<RepoMetrics>();
        var timeProvider = new TrackingFakeTimeProvider();
        var collectCount = 0;
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ExecuteAsyncFakeWorker(
            NewSingleRepoInventory(),
            cache,
            timeProvider,
            _ =>
            {
                // The callback intentionally updates the observation read after the worker stops.
                // ReSharper disable once AccessToModifiedClosure
                var attempt = Interlocked.Increment(ref collectCount);
                if (attempt == 1)
                {
                    firstAttemptStarted.TrySetResult();
                }
                else if (attempt == 2)
                {
                    secondAttemptStarted.TrySetResult();
                }

                throw new InvalidOperationException("cold-start sweep failure");
            }
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var initialState = await Task.WhenAny(firstAttemptStarted.Task, timeProvider.InitialDelayScheduled.Task)
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            if (initialState == timeProvider.InitialDelayScheduled.Task)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(15));
            }

            await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await timeProvider.RetryDelayScheduled.Task.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken
            );

            timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            _ = Volatile.Read(ref collectCount).Should().Be(1);

            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            _ = Volatile.Read(ref collectCount).Should().Be(2);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
