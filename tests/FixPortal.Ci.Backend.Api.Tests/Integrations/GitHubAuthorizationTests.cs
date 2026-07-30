using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubAuthorizationTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri RequestUri, string? Scheme, string? Parameter)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(
                (request.RequestUri!, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter)
            );
            var content = request.RequestUri!.AbsolutePath switch
            {
                var path when path.EndsWith("/actions/workflows", StringComparison.Ordinal) => """{"workflows":[]}""",
                var path when path.EndsWith("/runs", StringComparison.Ordinal) => """{"workflow_runs":[]}""",
                var path when path.EndsWith("/jobs", StringComparison.Ordinal) => """{"jobs":[]}""",
                "/search/issues" => """{"items":[]}""",
                _ => "[]",
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    [Fact]
    public async Task Public_requests_keep_pat_out_of_urls_and_send_it_as_bearer_authorization()
    {
        const string token = "pat-sentinel-7f2c";
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = token }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );

        _ = await client.ListRepositoriesAsync(CancellationToken.None);
        _ = await client.ListWorkflowsAsync("repo", CancellationToken.None);
        var workflow = new GitHubWorkflowDto(123, "CI", ".github/workflows/ci.yml", "active");
        _ = await client.GetRecentRunsAsync("repo", workflow, CancellationToken.None);
        _ = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        _ = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);
        _ = await client.GetRecentDefaultBranchRunsAsync("repo", 123, "main", 1, 1, CancellationToken.None);
        _ = await client.GetRunJobsAsync("repo", 456, CancellationToken.None);

        _ = handler.Requests.Should().HaveCount(7);
        foreach (var request in handler.Requests)
        {
            _ = request.RequestUri.ToString().Should().NotContain(token);
            _ = request.Scheme.Should().Be("Bearer");
            _ = request.Parameter.Should().Be(token);
        }
    }
}
