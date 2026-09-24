using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubPaginationTests
{
    private sealed class PagedHandler(Func<string, int, string> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            var page = path.EndsWith("page=2", StringComparison.Ordinal) ? 2 : 1;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response(path, page), Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private static GitHubOrgClient Client(PagedHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "test-token" }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );
    }

    private static string Json<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Repository_listing_follows_a_full_page_and_keeps_the_next_page()
    {
        var handler = new PagedHandler((_, page) => page == 1 ? Repositories(0, 100) : Repositories(100, 1));
        var client = Client(handler);

        var repositories = await client.ListRepositoriesAsync(TestContext.Current.CancellationToken);

        _ = repositories.Should().HaveCount(101);
        _ = repositories[100].Name.Should().Be("repo-100");
        _ = handler.Requests.Should().ContainSingle(path => path.EndsWith("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workflow_listing_follows_a_full_page_and_keeps_the_next_page()
    {
        var handler = new PagedHandler(
            (_, page) =>
                Json(
                    new
                    {
                        workflows = Enumerable
                            .Range(page == 1 ? 0 : 100, page == 1 ? 100 : 1)
                            .Select(index => new
                            {
                                id = index,
                                name = $"CI {index}",
                                path = $".github/workflows/ci-{index}.yml",
                                state = "active",
                            }),
                    }
                )
        );
        var client = Client(handler);

        var workflows = await client.ListWorkflowsAsync("repo", TestContext.Current.CancellationToken);

        _ = workflows.Should().HaveCount(101);
        _ = workflows[100].Name.Should().Be("CI 100");
        _ = handler.Requests.Should().ContainSingle(path => path.EndsWith("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Job_listing_follows_a_full_page_and_keeps_the_next_page()
    {
        var handler = new PagedHandler(
            (_, page) =>
                Json(
                    new
                    {
                        jobs = Enumerable
                            .Range(page == 1 ? 0 : 100, page == 1 ? 100 : 1)
                            .Select(index => new
                            {
                                name = $"job-{index}",
                                status = "completed",
                                conclusion = "success",
                            }),
                    }
                )
        );
        var client = Client(handler);

        var jobs = await client.GetRunJobsAsync("repo", 42, TestContext.Current.CancellationToken);

        _ = jobs.Should().HaveCount(101);
        _ = jobs[100].Name.Should().Be("job-100");
        _ = handler.Requests.Should().ContainSingle(path => path.EndsWith("page=2", StringComparison.Ordinal));
    }

    private static string Repositories(int first, int count) =>
        Json(
            Enumerable
                .Range(first, count)
                .Select(index => new
                {
                    name = $"repo-{index}",
                    html_url = $"https://github.com/FixPortal/repo-{index}",
                    @private = false,
                    archived = false,
                    default_branch = "main",
                })
        );
}
