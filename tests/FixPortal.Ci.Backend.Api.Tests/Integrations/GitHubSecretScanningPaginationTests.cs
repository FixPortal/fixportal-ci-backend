using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

/// <summary>
/// Pins the safety property of the secret-scanning pagination loop, raised as a review
/// finding on PR #85: a 404 arriving on page > 1 ends the listing and returns the count
/// accumulated so far, rather than reporting the repository unreadable.
/// </summary>
/// <remarks>
/// The finding is real -- that count IS truncated. What makes it acceptable is not
/// available by reading the loop alone, which is why it is asserted here rather than
/// argued in a comment: truncation can only be reached after a full page of 100 alerts,
/// so the resulting count is always >= 100, and every count above zero maps to
/// Outstanding. The state a leaked credential must never produce is Clean, and Clean
/// requires a first page that answers with zero alerts.
/// </remarks>
public sealed class GitHubSecretScanningPaginationTests : IDisposable
{
    // Every HttpClient this fixture hands to a client, so the test owns their lifetime
    // rather than leaving them to the finalizer. Flagged by GitHub Code Quality on PR #85.
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    private sealed class PagedSecretAlertsHandler(HttpStatusCode secondPageStatus) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var query = request.RequestUri!.Query;
            Queries.Add(query);

            // EndsWith, not Contains: the query is "?state=open&per_page=100&page=N", and
            // "per_page=100" CONTAINS "page=1" -- so a Contains match makes every page look
            // like page 1, the handler answers a full page forever, and the client pages
            // without end. That is exactly what happened: two `dotnet test` runs hung until
            // they were killed, against a suite that otherwise finishes in about a second.
            if (query.EndsWith("page=1", StringComparison.Ordinal))
            {
                var full = string.Join(',', Enumerable.Range(1, 100).Select(n => $$"""{"number":{{n}}}"""));
                return Task.FromResult(Json(HttpStatusCode.OK, $"[{full}]"));
            }
            return Task.FromResult(Json(secondPageStatus, secondPageStatus == HttpStatusCode.OK ? "[]" : ""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private GitHubOrgClient ClientFor(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        _clients.Add(http);
        return new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );
    }

    private static PrReviewFacts Facts() =>
        new(
            181,
            "chris",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );

    [Theory]
    // A 404 mid-listing is the reviewed edge case; an empty second page is the ordinary
    // exact-multiple-of-100 boundary. Both must land on the same count and the same state.
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task A_truncated_listing_still_reports_outstanding_never_clean(HttpStatusCode secondPageStatus)
    {
        var handler = new PagedSecretAlertsHandler(secondPageStatus);
        var client = ClientFor(handler);

        var count = await client.GetOpenSecretScanningAlertCountAsync("repo", TestContext.Current.CancellationToken);

        _ = count.Should().Be(100, "page 1 returned a full page before the listing ended");
        _ = handler.Queries.Should().HaveCount(2, "a full first page must be followed by a second request");

        var signal = ReviewSignalFactory.Build(
            Facts(),
            [new ReviewerOptions { Name = "Secret Scanning", Source = ReviewerSource.SecretScanning }],
            null,
            count,
            "https://github.com/FixPortal/repo/pull/181",
            "https://github.com/FixPortal/repo"
        )[0];

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.State.Should().NotBe(ReviewSignalState.Clean);
    }

    [Fact]
    public async Task An_unreadable_first_page_is_unknown_rather_than_a_truncated_count()
    {
        // The other half of the property: only page 1 can decide "unreadable", and it
        // must not be confused with an empty listing.
        var handler = new FirstPageFailsHandler();
        var client = ClientFor(handler);

        var count = await client.GetOpenSecretScanningAlertCountAsync("repo", TestContext.Current.CancellationToken);

        _ = count.Should().BeNull();
    }

    private sealed class FirstPageFailsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("", Encoding.UTF8, "application/json"),
                }
            );
    }
}
