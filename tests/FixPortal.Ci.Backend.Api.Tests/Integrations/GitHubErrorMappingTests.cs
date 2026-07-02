using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
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

    private static GitHubOrgClient NewClient(HttpStatusCode code, bool rateLimited = false, DashboardSnapshotState? state = null)
    {
        var http = new HttpClient(new StatusHandler(code, rateLimited)) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore(),
            state);
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

    // M4: a 429 is unconditional proof of rate limiting, so it must map to
    // GitHubRateLimitException even without X-RateLimit-Remaining / Retry-After —
    // otherwise it fell through to HttpRequestException and did not abort the batch.
    [Fact]
    public async Task A_429_without_rate_limit_headers_still_maps_to_GitHubRateLimitException()
    {
        var client = NewClient(HttpStatusCode.TooManyRequests, rateLimited: false);
        var act = async () => await client.ListRepositoriesAsync(CancellationToken.None);
        _ = await act.Should().ThrowAsync<GitHubRateLimitException>();
    }

    // B2: the best-effort PR endpoints (open-PR list, merged-PR search) need the
    // "Pull requests: Read" scope, which the primary workflow/run reads don't. A
    // token missing only that scope 403s here — it must NOT flip the global health
    // signal, or /api/health flaps Degraded then clears on the next primary 200.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PR_endpoint_auth_failure_does_not_set_global_auth_state(HttpStatusCode code)
    {
        var state = new DashboardSnapshotState();
        var client = NewClient(code, state: state);

        var listPrs = async () => await client.ListOpenPullRequestsAsync("repo", CancellationToken.None);
        var lastMerged = async () => await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = await listPrs.Should().ThrowAsync<GitHubAuthException>();
        _ = await lastMerged.Should().ThrowAsync<GitHubAuthException>();
        state.LastAuthError.Should().BeNull();
    }

    // The contrast case: a primary endpoint's auth failure DOES drive global health,
    // so a genuinely broken/insufficient token still surfaces as Degraded.
    [Fact]
    public async Task Primary_endpoint_auth_failure_sets_global_auth_state()
    {
        var state = new DashboardSnapshotState();
        var client = NewClient(HttpStatusCode.Forbidden, state: state);

        var act = async () => await client.ListRepositoriesAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<GitHubAuthException>();
        state.LastAuthError.Should().NotBeNull();
    }
}
