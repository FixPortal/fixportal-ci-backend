using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubWorkflowRunTests
{
    private sealed class WorkflowRunsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"workflow_runs":[
                          {"id":7,"status":"completed","conclusion":"success","html_url":"https://github.com/acme/repo/actions/runs/7","display_title":"Explicit title","run_number":7,"head_branch":"main","event":"push","updated_at":"2026-01-01T00:00:00Z"},
                          {"id":42,"status":"completed","conclusion":"success","display_title":"   ","run_number":8,"head_branch":"main","event":"push","updated_at":"2026-01-02T00:00:00Z"},
                          {"id":0,"status":"completed","conclusion":"success","display_title":"Workflow fallback","run_number":9,"head_branch":"main","event":"push","updated_at":"2026-01-03T00:00:00Z"}
                        ]}
                        """,
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
    }

    [Fact]
    public async Task GetRecentRunsAsync_uses_url_and_title_fallbacks_for_incomplete_GitHub_runs()
    {
        using var http = new HttpClient(new WorkflowRunsHandler()) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "acme", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );
        var workflow = new GitHubWorkflowDto(3, "Build", ".github/workflows/ci.yml", "active");

        var runs = await client.GetRecentRunsAsync("repo", workflow, CancellationToken.None);

        _ = runs.Should().HaveCount(3);
        _ = runs[0].HtmlUrl.Should().Be("https://github.com/acme/repo/actions/runs/7");
        _ = runs[1].HtmlUrl.Should().Be("https://github.com/acme/repo/actions/runs/42");
        _ = runs[2].HtmlUrl.Should().Be("https://github.com/acme/repo/actions/workflows/ci.yml");
        _ = runs[1].Title.Should().Be("Build");
    }
}
