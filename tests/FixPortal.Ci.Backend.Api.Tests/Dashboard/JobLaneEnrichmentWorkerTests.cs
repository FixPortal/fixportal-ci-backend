using System.Net;
using System.Reflection;
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

// CB-H8 (JobLaneEnrichmentWorker half): no test file exists for this class at all.
// Exercises CollectAsync (JobLaneEnrichmentWorker.cs:39-107) directly via reflection
// — it is protected, reached in production only through the base class's ExecuteAsync
// loop — so pagination and the soft-fail path can be pinned without paying for the
// base loop's 0-15s startup jitter.
public class JobLaneEnrichmentWorkerTests
{
    private sealed class LaneScanHandler : HttpMessageHandler
    {
        public int JobsCallCount;
        public string? FailWorkflowsForRepo;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

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
                // No jobs at all -> IsScanComplete never reports complete, so scanning
                // continues until the maxRuns bound in CollectWorkflowJobsAsync stops it.
                return Task.FromResult(JsonOk("""{"jobs":[]}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonOk(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static JobLaneEnrichmentWorker NewWorker(LaneScanHandler handler, int maxRunsToScan)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
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
                        Patterns = ["deploy"],
                        MaxRunsToScan = maxRunsToScan,
                    },
                ],
            }
        );
        var client = new GitHubOrgClient(http, gitHubOptions, laneOptions, new GitHubETagStore());
        var inventory = new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), laneOptions);
        var cache = new PerRepoCache<IReadOnlyList<JobSignal>>();
        return new JobLaneEnrichmentWorker(
            "deploys",
            client,
            inventory,
            cache,
            laneOptions,
            TimeProvider.System,
            NullLogger<JobLaneEnrichmentWorker>.Instance
        );
    }

    private static Task<IReadOnlyList<JobSignal>?> InvokeCollectAsync(
        JobLaneEnrichmentWorker worker,
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        var method = typeof(JobLaneEnrichmentWorker).GetMethod(
            "CollectAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        method.Should().NotBeNull("CollectAsync must still exist as JobLaneEnrichmentWorker's collection entry point");
        return (Task<IReadOnlyList<JobSignal>?>)method!.Invoke(worker, [repo, ct])!;
    }

    [Fact]
    public async Task CollectAsync_should_bound_run_scanning_by_MaxRunsToScan()
    {
        var handler = new LaneScanHandler();
        var worker = NewWorker(handler, maxRunsToScan: 3);
        var repo = new GitHubRepoDto("repo-a", "https://github.com/FixPortal/repo-a", false, false, "main");

        var result = await InvokeCollectAsync(worker, repo, TestContext.Current.CancellationToken);

        _ = result.Should().NotBeNull();
        _ = result!.Should().BeEmpty(); // no job ever matched the "deploy" pattern
        _ = handler.JobsCallCount.Should().Be(3);
    }

    [Fact]
    public async Task CollectAsync_should_soft_fail_to_null_when_the_workflow_list_call_fails()
    {
        var handler = new LaneScanHandler { FailWorkflowsForRepo = "repo-a" };
        var worker = NewWorker(handler, maxRunsToScan: 30);
        var repo = new GitHubRepoDto("repo-a", "https://github.com/FixPortal/repo-a", false, false, "main");

        var result = await InvokeCollectAsync(worker, repo, TestContext.Current.CancellationToken);

        // Soft-fail: null keeps the caller's prior cached value rather than aborting
        // the sweep for every other repo (RepoEnrichmentWorker<T>.RunSweepAsync).
        _ = result.Should().BeNull();
        _ = handler.JobsCallCount.Should().Be(0);
    }
}
