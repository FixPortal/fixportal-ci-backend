using System.Net;
using System.Reflection;
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            request.RequestUri!.AbsolutePath == "/search/issues"
                ? Task.FromResult(ErrorResponse())
                : throw new InvalidOperationException($"Unexpected GitHub request: {request.RequestUri}");

        private static HttpResponseMessage ErrorResponse() => new(HttpStatusCode.InternalServerError);
    }

    private static MergedPrEnrichmentWorker NewWorker(HttpClient http)
    {
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var inventory = new GitHubInventoryCache(
            client,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            dashboardOptions
        );

        return new MergedPrEnrichmentWorker(
            client,
            inventory,
            new PerRepoCache<MergedPullRequest>(),
            dashboardOptions,
            TimeProvider.System,
            NullLogger<MergedPrEnrichmentWorker>.Instance
        );
    }

    private static Task<MergedPullRequest?> InvokeCollectAsync(
        MergedPrEnrichmentWorker worker,
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        var method = typeof(MergedPrEnrichmentWorker).GetMethod(
            "CollectAsync",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        method.Should().NotBeNull("CollectAsync must remain the worker's collection entry point");
        return (Task<MergedPullRequest?>)method!.Invoke(worker, [repo, ct])!;
    }

    [Fact]
    public async Task CollectAsync_should_soft_fail_to_null_when_the_merged_pr_request_fails()
    {
        using var handler = new MergedPrFailureHandler();
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        using var worker = NewWorker(http);
        var repo = new GitHubRepoDto("repo-a", "https://github.com/FixPortal/repo-a", false, false, "main");

        var result = await InvokeCollectAsync(worker, repo, TestContext.Current.CancellationToken);

        _ = result.Should().BeNull();
    }
}
