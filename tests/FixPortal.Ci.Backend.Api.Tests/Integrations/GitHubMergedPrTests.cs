using System.Net;
using System.Text;
// Constant BaseAddress initialization is non-throwing here; using declarations keep the tests flat and dispose at scope exit.
// ReSharper disable UsingStatementResourceInitialization
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubMergedPrTests
{
    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        _responses.Count > 0 ? _responses.Dequeue() : "[]",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
        }
    }

    [Fact]
    public async Task GetLastMergedPullRequest_uses_search_api_with_is_merged_filter()
    {
        // Verifies search endpoint, single request, and is:pr+is:merged query.
        var body =
            """{"items":[{"number":42,"title":"Ship it","user":{"login":"alice"},"html_url":"https://github.com/FixPortal/repo/pull/42","pull_request":{"merged_at":"2026-05-30T11:00:00Z"}}]}""";
        var handler = new RecordingHandler(body);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var gitHub = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHub, dashboard, new GitHubETagStore());

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result.Should().NotBeNull();
        _ = result!.Number.Should().Be(42);
        _ = result.Repo.Should().Be("repo");
        _ = handler.RequestUris.Should().HaveCount(1);
        _ = handler.RequestUris[0].AbsolutePath.Should().EndWith("search/issues");
        _ = handler.RequestUris[0].Query.Should().Contain("sort=updated");
        _ = handler.RequestUris[0].Query.Should().Contain("is%3Amerged");
        _ = handler.RequestUris[0].Query.Should().Contain("is%3Apr");
    }

    [Fact]
    public async Task GetLastMergedPullRequest_picks_max_merged_at_from_search_window()
    {
        // First result (most-recently-updated) may not have the greatest merged_at —
        // client-side MaxBy must prefer the later merge regardless of position in results.
        var body = """
            {"items":[
                {"number":10,"title":"Stale comment","user":{"login":"alice"},"html_url":"https://github.com/FixPortal/repo/pull/10","pull_request":{"merged_at":"2026-05-01T10:00:00Z"}},
                {"number":20,"title":"Latest merge","user":{"login":"bob"},"html_url":"https://github.com/FixPortal/repo/pull/20","pull_request":{"merged_at":"2026-05-30T11:00:00Z"}},
                {"number":30,"title":"Middle merge","user":{"login":"carol"},"html_url":"https://github.com/FixPortal/repo/pull/30","pull_request":{"merged_at":"2026-05-15T09:00:00Z"}}
            ]}
            """;
        var handler = new RecordingHandler(body);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var gitHub = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHub, dashboard, new GitHubETagStore());

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result.Should().NotBeNull();
        _ = result!.Number.Should().Be(20);
        _ = result.Author.Should().Be("bob");
        _ = handler.RequestUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetLastMergedPullRequest_returns_null_when_no_merged_prs_found()
    {
        var handler = new RecordingHandler("""{"items":[]}""");
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var gitHub = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHub, dashboard, new GitHubETagStore());

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result.Should().BeNull();
    }

    private const int SearchPageSize = GitHubOrgClient.SearchPageSize; // full-page fixtures track the production page size
    private static readonly Instant TimeBase = Instant.FromUtc(2026, 5, 1, 0, 0);

    private static Instant At(long offsetSeconds) => TimeBase + Duration.FromSeconds(offsetSeconds);

    // Builds a search/issues response page from (number, mergedAtOffset?, updatedAtOffset) tuples.
    private static string SearchPage(params (int Number, long? Merged, long Updated)[] items)
    {
        var elems = items.Select(i =>
        {
            var pr = i.Merged is { } m ? $$"""{"merged_at":"{{InstantPattern.ExtendedIso.Format(At(m))}}"}""" : "null";
            return $$"""{"number":{{i.Number}},"user":{"login":"u"},"html_url":"https://github.com/FixPortal/repo/pull/{{i.Number}}","pull_request":{{pr}},"updated_at":"{{InstantPattern.ExtendedIso.Format(At(i.Updated))}}"}""";
        });
        return $$"""{"items":[{{string.Join(",", elems)}}]}""";
    }

    private static GitHubOrgClient NewClient(HttpClient http) =>
        new(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );

    [Fact]
    public async Task GetLastMergedPullRequest_accumulates_max_merge_across_pages()
    {
        // Page 1 is a *full* page of unmerged PRs: the search finds no merge and
        // cannot terminate, so it must page on to find the merged PR on page 2.
        var page1 = SearchPage(
            Enumerable
                .Range(0, SearchPageSize)
                .Select(k => (Number: 100 + k, Merged: (long?)null, Updated: 3000L - k))
                .ToArray()
        );
        var page2 = SearchPage((42, 2500L, 2980L));
        var handler = new RecordingHandler(page1, page2);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = NewClient(http);

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result.Should().NotBeNull();
        _ = result!.Number.Should().Be(42);
        _ = result.MergedAt.Should().Be(At(2500));
        _ = handler.RequestUris.Should().HaveCount(2); // both pages fetched
        _ = handler.RequestUris[1].Query.Should().Contain("page=2");
    }

    [Fact]
    public async Task GetLastMergedPullRequest_stops_once_best_merge_dominates_the_window()
    {
        // A full first page whose best merged_at (4985) is >= the oldest updated_at
        // on the page (4981). Because the API sorts by updated_at desc and
        // merged_at <= updated_at, no later page can beat it — the search must stop
        // and NOT fetch page 2. The queued page 2 (an impossible merge that violates
        // that invariant) proves the early exit: it is never read.
        var page1 = SearchPage(
            Enumerable
                .Range(0, SearchPageSize)
                .Select(k => (Number: 200 + k, Merged: (long?)(k == 0 ? 4985L : null), Updated: 5000L - k))
                .ToArray()
        );
        var unreachablePage2 = SearchPage((999, 4990L, 4980L));
        var handler = new RecordingHandler(page1, unreachablePage2);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = NewClient(http);

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result!.Number.Should().Be(200);
        _ = result.MergedAt.Should().Be(At(4985));
        _ = handler.RequestUris.Should().HaveCount(1); // terminated; page 2 never fetched
    }

    [Fact]
    public async Task GetLastMergedPullRequest_stops_on_short_page_without_fetching_again()
    {
        // A partial first page (fewer than perPage items) is the last page; the
        // search must not request a second page even when the merge (500) does not
        // dominate the window (updated 1000), so the stop is the short-page break,
        // not the dominate-the-window termination.
        var page1 = SearchPage((7, 500L, 1000L));
        var handler = new RecordingHandler(page1, SearchPage((8, 2000L, 2000L)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = NewClient(http);

        var result = await client.GetLastMergedPullRequestAsync("repo", CancellationToken.None);

        _ = result!.Number.Should().Be(7);
        _ = handler.RequestUris.Should().HaveCount(1);
    }
}
