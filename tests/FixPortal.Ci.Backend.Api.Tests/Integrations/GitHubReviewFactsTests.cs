using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubReviewFactsTests
{
    // Default head oid shared by Pull/Review/Thread below so ordinary fixtures land on
    // the head commit without every test having to plumb it through by hand. Tests that
    // care about commit-scoping override it explicitly.
    private const string HeadOid = "head-sha";

    private static ReviewFactsPull Pull(
        IReadOnlyList<GraphQlThread>? threads = null,
        IReadOnlyList<GraphQlReview>? reviews = null,
        IReadOnlyList<GraphQlLabel>? labels = null,
        IReadOnlyList<GraphQlContext>? checks = null,
        string? headOid = HeadOid
    ) =>
        new(
            181,
            new GraphQlActor("chris"),
            new NodeList<GraphQlLabel>(labels ?? []),
            new NodeList<GraphQlReview>(reviews ?? []),
            new NodeList<GraphQlThread>(threads ?? []),
            headOid is null
                ? new NodeList<GraphQlCommitNode>([])
                : new NodeList<GraphQlCommitNode>([
                    new GraphQlCommitNode(
                        new GraphQlCommit(headOid, new GraphQlRollup(new NodeList<GraphQlContext>(checks ?? [])))
                    ),
                ])
        );

    private static GraphQlReview Review(string author, string? commitOid = HeadOid) =>
        new(new GraphQlActor(author), commitOid is null ? null : new GraphQlCommit(commitOid, null));

    private static GraphQlThread Thread(string author, bool resolved, string? commitOid = HeadOid) =>
        new(
            resolved,
            new NodeList<GraphQlComment>([
                new GraphQlComment(
                    new GraphQlActor(author),
                    commitOid is null ? null : new GraphQlCommit(commitOid, null)
                ),
            ])
        );

    [Fact]
    public void Counts_only_unresolved_threads_and_keys_them_by_the_first_comment_author()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(
                threads:
                [
                    Thread("coderabbitai", false),
                    Thread("coderabbitai", false),
                    Thread("coderabbitai", true),
                    Thread("chris", false),
                ]
            )
        );

        _ = facts.UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(2);
        _ = facts.UnresolvedThreadsByAuthor["chris"].Should().Be(1);
    }

    [Fact]
    public void A_review_on_the_head_commit_counts_as_head_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(reviews: [Review("gitar-app", commitOid: HeadOid)], headOid: HeadOid)
        );

        _ = facts.HeadParticipatingAuthors.Should().Contain("gitar-app");
    }

    [Fact]
    public void A_review_on_an_older_commit_does_not_count_as_head_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(reviews: [Review("gitar-app", commitOid: "old-sha")], headOid: HeadOid)
        );

        _ = facts.HeadParticipatingAuthors.Should().NotContain("gitar-app");
    }

    [Fact]
    public void A_thread_comment_on_the_head_commit_counts_as_head_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(threads: [Thread("coderabbitai", resolved: true, commitOid: HeadOid)], headOid: HeadOid)
        );

        _ = facts.HeadParticipatingAuthors.Should().Contain("coderabbitai");
    }

    [Fact]
    public void A_thread_comment_on_an_older_commit_does_not_count_as_head_participation_but_still_counts_when_unresolved()
    {
        // Unresolved-thread counting is unchanged by head-scoping: an open finding from
        // an older commit is still open and must still produce Outstanding.
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(threads: [Thread("coderabbitai", resolved: false, commitOid: "old-sha")], headOid: HeadOid)
        );

        _ = facts.HeadParticipatingAuthors.Should().NotContain("coderabbitai");
        _ = facts.UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(1);
    }

    [Fact]
    public void A_null_head_oid_yields_no_head_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(reviews: [Review("gitar-app")], threads: [Thread("coderabbitai", resolved: true)], headOid: null)
        );

        _ = facts.HeadParticipatingAuthors.Should().BeEmpty();
    }

    [Fact]
    public void Records_labels_and_the_author_login()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(labels: [new GraphQlLabel("review-high")]));

        _ = facts.Number.Should().Be(181);
        _ = facts.AuthorLogin.Should().Be("chris");
        _ = facts.Labels.Should().Contain("review-high");
    }

    [Fact]
    public void Records_only_successful_check_app_slugs()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(
                checks:
                [
                    new GraphQlContext("SUCCESS", new GraphQlCheckSuite(new GraphQlApp("github-code-scanning"))),
                    new GraphQlContext("FAILURE", new GraphQlCheckSuite(new GraphQlApp("some-app"))),
                ]
            )
        );

        _ = facts.SuccessfulCheckAppSlugs.Should().Contain("github-code-scanning");
        _ = facts.SuccessfulCheckAppSlugs.Should().NotContain("some-app");
    }

    [Fact]
    public void Survives_a_payload_with_null_collections_and_a_null_author()
    {
        var pull = new ReviewFactsPull(9, null, null, null, null, null);

        var facts = GitHubOrgClient.ToReviewFacts(pull);

        _ = facts.AuthorLogin.Should().Be("unknown");
        _ = facts.Labels.Should().BeEmpty();
        _ = facts.UnresolvedThreadsByAuthor.Should().BeEmpty();
    }

    [Fact]
    public void Matches_logins_and_labels_case_insensitively_so_config_casing_cannot_silently_miss()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(
                threads: [Thread("CodeRabbitAI", false)],
                labels: [new GraphQlLabel("Review-High")],
                checks: [new GraphQlContext("SUCCESS", new GraphQlCheckSuite(new GraphQlApp("GitHub-Code-Scanning")))]
            )
        );

        _ = facts.UnresolvedThreadsByAuthor.ContainsKey("coderabbitai").Should().BeTrue();
        _ = facts.HeadParticipatingAuthors.Contains("coderabbitai").Should().BeTrue();
        _ = facts.Labels.Contains("review-high").Should().BeTrue();
        _ = facts.SuccessfulCheckAppSlugs.Contains("github-code-scanning").Should().BeTrue();
    }
}

public class GitHubReviewFactsTransportTests
{
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        // The request (and its content) is disposed as soon as the client returns, so
        // the body has to be captured here rather than read off Requests afterwards.
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return responses.Dequeue();
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

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Posts_to_graphql_and_parses_camel_case_field_names()
    {
        const string body = """
            {"data":{"repository":{"pullRequests":{"nodes":[
              {"number":181,"author":{"login":"chris"},
               "labels":{"nodes":[{"name":"review-high"}]},
               "reviews":{"nodes":[]},
               "reviewThreads":{"nodes":[{"isResolved":false,"comments":{"nodes":[{"author":{"login":"coderabbitai"}}]}}]},
               "commits":{"nodes":[]}}
            ]}}}}
            """;
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([Json(body)]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        var facts = await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _ = handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/graphql");
        _ = facts[181].Labels.Should().Contain("review-high");
        _ = facts[181].UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(1);
    }

    [Fact]
    public async Task Selects_originalCommit_on_thread_comments_and_asks_for_the_rate_limit()
    {
        // The whole head-scoping design lives in the query TEXT, which no other test
        // reads: reverting `originalCommit` to `commit` would regress a false-Clean bug
        // with a green suite, because `commit` tracks forward onto the new head as a PR
        // is pushed to and would make a stale thread look like head participation.
        // rateLimit is asserted alongside it for the same reason — it is only observable
        // in the serialized request.
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([Json("""{"data":{"repository":{"pullRequests":{"nodes":[]}}}}""")])
        );
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");

        _ = await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        var body = handler.Bodies.Should().ContainSingle().Subject;
        _ = body.Should().Contain("nodes { author { login } originalCommit { oid } }");
        _ = body.Should().Contain("rateLimit { cost remaining resetAt }");
    }

    [Fact]
    public async Task Records_the_graphql_rate_limit_reported_by_the_query()
    {
        const string body = """
            {"data":{"rateLimit":{"cost":7,"remaining":4993,"resetAt":"2026-07-31T12:00:00Z"},
             "repository":{"pullRequests":{"nodes":[]}}}}
            """;
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([Json(body)]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var client = CreateClient(http);

        _ = await client.GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = client.LastGraphQlRateLimit.Should().Be(new GraphQlRateLimit(7, 4993, "2026-07-31T12:00:00Z"));
    }

    [Fact]
    public async Task Warns_when_a_repo_overflows_the_pull_request_cap()
    {
        // pullRequests(first: 25) truncates silently beyond page 1; the hasNextPage
        // warning is the only place an overflowing repo becomes observable. The query
        // must also keep asking for pageInfo, or the warning can never fire.
        const string body = """
            {"data":{"repository":{"pullRequests":{"pageInfo":{"hasNextPage":true},"nodes":[]}}}}
            """;
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([Json(body)]));
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var logger = new CapturingLogger<GitHubOrgClient>();

        _ = await CreateClient(http, logger).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = handler.Bodies.Should().ContainSingle().Subject.Should().Contain("pageInfo { hasNextPage }");
        _ = logger
            .Entries.Should()
            .Contain(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("more than 25 open pull requests", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task Throws_when_graphql_reports_errors_in_a_200_response()
    {
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([
                Json("""{"data":null,"errors":[{"message":"Could not resolve to a Repository"}]}"""),
            ])
        );
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var client = CreateClient(http);

        var act = async () => await client.GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }
}
