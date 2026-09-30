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
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class MergedPrEnrichmentWorkerTests
{
    private sealed class MergedPrFailureHandler : HttpMessageHandler
    {
        public int SearchCallCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.AbsolutePath == "/orgs/FixPortal/repos")
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
            if (request.RequestUri.AbsolutePath == "/search/issues")
            {
                _ = Interlocked.Increment(ref SearchCallCount);
                return Task.FromResult(ErrorResponse());
            }
            throw new InvalidOperationException($"Unexpected GitHub request: {request.RequestUri}");
        }

        private static HttpResponseMessage ErrorResponse() => new(HttpStatusCode.InternalServerError);
    }

    private static MergedPrEnrichmentWorker NewWorker(
        HttpClient http,
        PerRepoCache<MergedPullRequest> cache,
        TrackingFakeTimeProvider timeProvider
    )
    {
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(
            new DashboardOptions
            {
                SnapshotPath = "s.json",
                RefreshSeconds = 60,
                MergedPrEnabled = true,
                MergedPrRefreshSeconds = 60,
            }
        );
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var inventory = new GitHubInventoryCache(
            client,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            dashboardOptions
        );

        return new MergedPrEnrichmentWorker(
            client,
            inventory,
            cache,
            dashboardOptions,
            timeProvider,
            NullLogger<MergedPrEnrichmentWorker>.Instance
        );
    }

    [Fact]
    public async Task ColdStartSweep_should_preserve_cached_merged_pr_when_the_request_fails()
    {
        using var handler = new MergedPrFailureHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.github.com/");
        var cache = new PerRepoCache<MergedPullRequest>();
        var previous = new MergedPullRequest(
            7,
            "Previous",
            "chris",
            "repo-a",
            "https://x/7",
            Instant.FromUnixTimeSeconds(1)
        );
        cache.Update("repo-a", previous);
        var timeProvider = new TrackingFakeTimeProvider();
        using var worker = NewWorker(http, cache, timeProvider);

        var ct = TestContext.Current.CancellationToken;
        await worker.StartAsync(ct);
        try
        {
            await timeProvider.InitialDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            timeProvider.Advance(TimeSpan.FromSeconds(15));
            var steadyState = timeProvider.SteadyStateTimerScheduled.Task;
            var result = await Task.WhenAny(steadyState, timeProvider.RetryDelayScheduled.Task)
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
            _ = result.Should().BeSameAs(steadyState, "a handled fetch failure keeps the cold-start sweep successful");
        }
        finally
        {
            await worker.StopAsync(ct);
        }

        _ = handler.SearchCallCount.Should().Be(1);
        _ = cache.TryGet("repo-a", out var resultValue).Should().BeTrue();
        _ = resultValue.Should().BeSameAs(previous);
    }
}
