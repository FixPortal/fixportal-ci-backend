using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubCodeScanningTests
{
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private static GitHubOrgClient CreateClient(HttpClient http) =>
        new(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 }),
            new GitHubETagStore()
        );

    private static HttpClient Responding(HttpStatusCode status, string body = "[]")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return new HttpClient(new ScriptedHandler(new Queue<HttpResponseMessage>([response])))
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
    }

    [Theory]
    [InlineData("refs/pull/181/head", 181)]
    [InlineData("refs/pull/7/merge", 7)]
    [InlineData("refs/heads/main", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("refs/pull/notanumber/head", null)]
    public void PullNumberFromRef_extracts_only_pull_request_refs(string? gitRef, int? expected)
    {
        _ = GitHubOrgClient.PullNumberFromRef(gitRef).Should().Be(expected);
    }

    [Fact]
    public async Task Buckets_open_alerts_by_pull_request_number()
    {
        const string body = """
            [
              {"most_recent_instance":{"ref":"refs/pull/181/head"}},
              {"most_recent_instance":{"ref":"refs/pull/181/head"}},
              {"most_recent_instance":{"ref":"refs/pull/179/head"}},
              {"most_recent_instance":{"ref":"refs/heads/main"}}
            ]
            """;
        using var http = Responding(HttpStatusCode.OK, body);

        var counts = await CreateClient(http).GetOpenCodeScanningAlertCountsAsync("repo", CancellationToken.None);

        _ = counts.Should().NotBeNull();
        _ = counts![181].Should().Be(2);
        _ = counts[179].Should().Be(1);
        _ = counts.Should().HaveCount(2);
    }

    [Fact]
    public async Task Follows_pagination_so_alerts_past_the_first_page_are_still_counted()
    {
        // A repo with more than 100 open alerts pushes a PR's alerts off page 1. Without
        // the page loop the PR reads zero alerts, and with a successful code-scanning
        // check on head that renders Clean with alerts outstanding.
        var firstPage =
            "["
            + string.Join(",", Enumerable.Repeat("""{"most_recent_instance":{"ref":"refs/heads/main"}}""", 100))
            + "]";
        const string secondPage = """[{"most_recent_instance":{"ref":"refs/pull/181/head"}}]""";
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([JsonOk(firstPage), JsonOk(secondPage)]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        var counts = await CreateClient(http).GetOpenCodeScanningAlertCountsAsync("repo", CancellationToken.None);

        _ = counts.Should().NotBeNull();
        _ = counts![181].Should().Be(1);
        _ = handler.Urls.Should().HaveCount(2);
        _ = handler.Urls[1].Should().Contain("page=2");
    }

    private static HttpResponseMessage JsonOk(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Reports_unavailable_rather_than_zero_when_scanning_cannot_be_read(HttpStatusCode status)
    {
        using var http = Responding(status);

        var counts = await CreateClient(http).GetOpenCodeScanningAlertCountsAsync("repo", CancellationToken.None);

        // Null, not empty: an empty dictionary would render the CodeQL pill green,
        // claiming a clean scan that never ran.
        _ = counts.Should().BeNull();
    }
}
