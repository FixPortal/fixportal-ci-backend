using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging;
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

    private static GitHubOrgClient CreateClient(HttpClient http, ILogger<GitHubOrgClient>? logger = null) =>
        new(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 }),
            new GitHubETagStore(),
            logger: logger
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
    public async Task Queries_each_pull_request_with_the_pr_filter_and_attributes_counts_by_the_requested_number()
    {
        // With pr=N GitHub reports each alert's most_recent_instance against the default
        // branch ref, so the count must come from the number asked for, not the instance ref.
        const string pr181 = """
            [
              {"most_recent_instance":{"ref":"refs/heads/main"}},
              {"most_recent_instance":{"ref":"refs/heads/main"}}
            ]
            """;
        const string pr179 = """[{"most_recent_instance":{"ref":"refs/heads/main"}}]""";
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([JsonOk(pr181), JsonOk(pr179), JsonOk("[]")]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        var counts = await CreateClient(http)
            .GetOpenCodeScanningAlertCountsAsync("repo", [181, 179, 7], CancellationToken.None);

        _ = counts.Should().NotBeNull();
        _ = counts[181].Should().Be(2);
        _ = counts[179].Should().Be(1);
        _ = counts.Should().HaveCount(2);
        _ = handler.Urls.Should().HaveCount(3);
        _ = handler.Urls[0].Should().Contain("pr=181");
        _ = handler.Urls[1].Should().Contain("pr=179");
        _ = handler.Urls[2].Should().Contain("pr=7");
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
        const string secondPage = """[{"most_recent_instance":{"ref":"refs/heads/main"}}]""";
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([JsonOk(firstPage), JsonOk(secondPage)]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        var counts = await CreateClient(http)
            .GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);

        _ = counts.Should().NotBeNull();
        _ = counts[181].Should().Be(101);
        _ = handler.Urls.Should().HaveCount(2);
        _ = handler.Urls[1].Should().Contain("page=2");
    }

    [Fact]
    public async Task Returns_unavailable_when_a_later_code_scanning_page_cannot_be_read()
    {
        var firstPage =
            "["
            + string.Join(",", Enumerable.Repeat("""{"most_recent_instance":{"ref":"refs/pull/181/head"}}""", 100))
            + "]";
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([JsonOk(firstPage), new HttpResponseMessage(HttpStatusCode.NotFound)])
        );
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        var counts = await CreateClient(http)
            .GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);

        _ = counts.Should().BeNull();
        _ = handler.Urls.Should().HaveCount(2);
    }

    private static HttpResponseMessage JsonOk(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Reports_unavailable_rather_than_zero_when_scanning_cannot_be_read(HttpStatusCode status)
    {
        using var http = Responding(status);

        var counts = await CreateClient(http)
            .GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);

        // Null, not empty: an empty dictionary would render the CodeQL pill green,
        // claiming a clean scan that never ran.
        _ = counts.Should().BeNull();
    }

    [Fact]
    public async Task A_successful_read_clears_the_one_time_permission_warning_so_a_regression_re_warns()
    {
        // 403 (scope missing) -> OK (operator fixed the PAT) -> 403 (regressed). The
        // warning must fire for BOTH failures: if a successful read did not clear the
        // warned-flag, the second 403 would be silent until a process restart.
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([
                new HttpResponseMessage(HttpStatusCode.Forbidden),
                JsonOk("""[{"most_recent_instance":{"ref":"refs/pull/181/head"}}]"""),
                new HttpResponseMessage(HttpStatusCode.Forbidden),
            ])
        );
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var logger = new CapturingLogger<GitHubOrgClient>();
        var client = CreateClient(http, logger);

        _ = await client.GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);
        var recovered = await client.GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);
        _ = await client.GetOpenCodeScanningAlertCountsAsync("repo", [181], CancellationToken.None);

        _ = recovered.Should().NotBeNull();
        _ = recovered[181].Should().Be(1);
        _ = logger.Entries.Count(e => e.Level == LogLevel.Warning).Should().Be(2);
        _ = logger
            .Entries.Should()
            .Contain(e =>
                e.Level == LogLevel.Information && e.Message.Contains("readable again", StringComparison.Ordinal)
            );
    }
}
