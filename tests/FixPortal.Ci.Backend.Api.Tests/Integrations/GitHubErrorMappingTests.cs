using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

// Pins the status-code -> exception contract that DashboardRefreshService relies on:
// a 401/403 (non-rate-limited) becomes GitHubAuthException (caught per-repo / treated
// as a missing PR scope), a 5xx becomes HttpRequestException (best-effort PR
// degradation), and a rate-limited 403 becomes GitHubRateLimitException (aborts the
// batch). If this contract changes, the refresh-service catch clauses break silently.
public class GitHubErrorMappingTests
{
    private sealed class StatusHandler(HttpStatusCode code, bool rateLimited = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(code)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            };
            if (rateLimited)
            {
                _ = response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            }
            return Task.FromResult(response);
        }
    }

    private static GitHubOrgClient NewClient(HttpStatusCode code, bool rateLimited = false)
    {
        var http = new HttpClient(new StatusHandler(code, rateLimited)) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ListOpenPullRequests_maps_auth_failures_to_GitHubAuthException(HttpStatusCode code)
    {
        var client = NewClient(code);
        var act = async () => await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        _ = await act.Should().ThrowAsync<GitHubAuthException>();
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_a_5xx_to_HttpRequestException()
    {
        var client = NewClient(HttpStatusCode.InternalServerError);
        var act = async () => await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListOpenPullRequests_maps_a_rate_limited_403_to_GitHubRateLimitException()
    {
        var client = NewClient(HttpStatusCode.Forbidden, rateLimited: true);
        var act = async () => await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        _ = await act.Should().ThrowAsync<GitHubRateLimitException>();
    }
}
