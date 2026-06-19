using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

// Exercises the conditional-request (If-None-Match / 304) path through
// GitHubOrgClient's private SendAsync, driven via a public method. A 304 is free
// against GitHub's primary rate limit, so this caching is what lets the dashboard
// poll on a 20s cadence without exhausting the PAT budget.
public class GitHubETagCachingTests
{
    private const string PullsUrl = "repos/FixPortal/repo/pulls?state=open&per_page=100&page=1";

    // Hands back canned responses in order and records each request that triggered
    // one, so a test can assert the conditional header the client sent.
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private static GitHubOrgClient CreateClient(HttpClient http, GitHubETagStore store)
    {
        var gitHub = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        return new GitHubOrgClient(http, gitHub, dashboard, store);
    }

    private static HttpResponseMessage Ok(string body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static string PullBody(int number, string title) =>
        $$"""[{"number":{{number}},"title":"{{title}}","draft":false,"created_at":"2026-01-01T00:00:00Z"}]""";

    [Fact]
    public async Task First_request_sends_no_conditional_header_and_stores_the_etag()
    {
        var store = new GitHubETagStore();
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([Ok(PullBody(1, "one"), "W/\"abc\"")]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = CreateClient(http, store);

        var prs = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);

        _ = prs.Should().ContainSingle().Which.Number.Should().Be(1);
        _ = handler.Requests[0].Headers.IfNoneMatch.Should().BeEmpty();
        _ = store.Get(PullsUrl).Should().NotBeNull();
    }

    [Fact]
    public async Task Second_request_revalidates_with_if_none_match_and_serves_cache_on_304()
    {
        var store = new GitHubETagStore();
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>(
        [
            Ok(PullBody(1, "one"), "W/\"abc\""),
            new HttpResponseMessage(HttpStatusCode.NotModified),
        ]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = CreateClient(http, store);

        _ = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        var second = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);

        _ = second.Should().ContainSingle().Which.Title.Should().Be("one");
        _ = handler.Requests[1].Headers.IfNoneMatch.ToString().Should().Be("W/\"abc\"");
    }

    [Fact]
    public async Task Changed_resource_returns_fresh_body_and_updates_the_stored_etag()
    {
        var store = new GitHubETagStore();
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>(
        [
            Ok(PullBody(1, "old"), "W/\"v1\""),
            Ok(PullBody(2, "new"), "W/\"v2\""),
        ]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = CreateClient(http, store);

        _ = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        var second = await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);

        _ = second.Should().ContainSingle().Which.Number.Should().Be(2);
        _ = store.Get(PullsUrl)!.ETag.ToString().Should().Be("W/\"v2\"");
    }
}
