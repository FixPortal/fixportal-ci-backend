using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// Exercises the worker's cold-start sweep and its observable cache writes through the
// public BackgroundService lifecycle.
public sealed class JobLaneEnrichmentWorkerTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

    private sealed class LaneScanHandler : HttpMessageHandler
    {
        public int JobsCallCount;
        public string? FailWorkflowsForRepo;
        public bool MatchDeployJob;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/orgs/FixPortal/repos")
            {
                return Task.FromResult(
                    JsonOk(
                        """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]"""
                    )
                );
            }

            if (path.EndsWith("/actions/workflows", StringComparison.Ordinal))
            {
                var repo = Regex.Match(path, "^/repos/[^/]+/(?<repo>[^/]+)/actions/workflows$").Groups["repo"].Value;
                if (repo == FailWorkflowsForRepo)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(
                    JsonOk(
                        """{"workflows":[{"id":1,"name":"CI","path":".github/workflows/ci.yml","state":"active"}]}"""
                    )
                );
            }

            if (
                path.Contains("/actions/workflows/", StringComparison.Ordinal)
                && path.EndsWith("/runs", StringComparison.Ordinal)
            )
            {
                // Always a full page of 10 completed-but-unmatched runs, so pagination
                // continues purely on the MaxRunsToScan bound rather than exhausting
                // (page.Count < pageSize) or completing early (a matching job signal).
                var runs = string.Join(
                    ",",
                    Enumerable
                        .Range(0, 10)
                        .Select(i =>
                            $$"""{"id":{{i + 1}},"html_url":"https://x/{{i}}","status":"completed","conclusion":"success"}"""
                        )
                );
                return Task.FromResult(JsonOk($$"""{"workflow_runs":[{{runs}}]}"""));
            }

            if (path.Contains("/jobs", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref JobsCallCount);
                if (MatchDeployJob)
                {
                    return Task.FromResult(
                        JsonOk(
                            """{"jobs":[{"id":9,"name":"Deploy production","status":"completed","conclusion":"success","html_url":"https://x/job/9"}]}"""
                        )
                    );
                }
                // No jobs at all -> IsScanComplete never reports complete, so scanning
                // continues until the maxRuns bound in CollectWorkflowJobsAsync stops it.
                return Task.FromResult(JsonOk("""{"jobs":[]}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonOk(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private JobLaneEnrichmentWorker NewWorker(
        LaneScanHandler handler,
        PerRepoCache<IReadOnlyList<JobSignal>> cache,
        TrackingFakeTimeProvider timeProvider,
        int maxRunsToScan
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        _httpClients.Add(http);
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var laneOptions = Options.Create(
            new DashboardOptions
            {
                SnapshotPath = "s.json",
                RefreshSeconds = 60,
                JobLanes =
                [
                    new JobLaneOptions
                    {
                        Key = "deploys",
                        Label = "Deploys",
                        RefreshSeconds = 60,
                        Patterns = ["deploy"],
                        MaxRunsToScan = maxRunsToScan,
                    },
                ],
            }
        );
        var client = new GitHubOrgClient(http, gitHubOptions, laneOptions, new GitHubETagStore());
        var inventory = new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), laneOptions);
        return new JobLaneEnrichmentWorker(
            "deploys",
            client,
            inventory,
            cache,
            laneOptions,
            timeProvider,
            NullLogger<JobLaneEnrichmentWorker>.Instance
        );
    }

    private static async Task RunColdStartSweepAsync(
        JobLaneEnrichmentWorker worker,
        TrackingFakeTimeProvider timeProvider
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await worker.StartAsync(ct);
        try
        {
            await timeProvider.InitialDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            timeProvider.Advance(TimeSpan.FromSeconds(15));
            var result = await Task.WhenAny(
                    timeProvider.SteadyStateTimerScheduled.Task,
                    timeProvider.RetryDelayScheduled.Task
                )
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (result == timeProvider.RetryDelayScheduled.Task)
            {
                throw new InvalidOperationException("The cold-start sweep failed and scheduled a retry.");
            }
        }
        finally
        {
            await worker.StopAsync(ct);
        }
    }

    [Fact]
    public async Task ColdStartSweep_should_bound_run_scanning_by_MaxRunsToScan()
    {
        var handler = new LaneScanHandler();
        var cache = new PerRepoCache<IReadOnlyList<JobSignal>>();
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = NewWorker(handler, cache, timeProvider, maxRunsToScan: 3);

        await RunColdStartSweepAsync(worker, timeProvider);

        _ = cache.TryGet("repo-a", out var result).Should().BeTrue();
        _ = result.Should().BeEmpty(); // no job ever matched the "deploy" pattern
        _ = handler.JobsCallCount.Should().Be(3);
    }

    [Fact]
    public async Task ColdStartSweep_should_preserve_cached_signals_when_workflow_listing_fails()
    {
        var handler = new LaneScanHandler { FailWorkflowsForRepo = "repo-a" };
        var cache = new PerRepoCache<IReadOnlyList<JobSignal>>();
        IReadOnlyList<JobSignal> previous =
        [
            new JobSignal(
                "CI",
                "Deploy previous",
                SignalState.Success,
                "https://x/previous",
                Instant.FromUnixTimeSeconds(1)
            ),
        ];
        cache.Update("repo-a", previous);
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = NewWorker(handler, cache, timeProvider, maxRunsToScan: 30);

        await RunColdStartSweepAsync(worker, timeProvider);

        _ = cache.TryGet("repo-a", out var result).Should().BeTrue();
        _ = result.Should().BeSameAs(previous);
        _ = handler.JobsCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ColdStartSweep_should_cache_matching_signals_from_workflow_jobs()
    {
        var handler = new LaneScanHandler { MatchDeployJob = true };
        var cache = new PerRepoCache<IReadOnlyList<JobSignal>>();
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = NewWorker(handler, cache, timeProvider, maxRunsToScan: 30);

        await RunColdStartSweepAsync(worker, timeProvider);

        _ = cache.TryGet("repo-a", out var result).Should().BeTrue();
        _ = result.Should().ContainSingle().Which.Name.Should().Be("Deploy production");
    }
}
