using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubReviewFactsTests
{
    private static ReviewFactsPull Pull(
        IReadOnlyList<GraphQlThread>? threads = null,
        IReadOnlyList<GraphQlReview>? reviews = null,
        IReadOnlyList<GraphQlLabel>? labels = null,
        IReadOnlyList<GraphQlContext>? checks = null
    ) =>
        new(
            181,
            new GraphQlActor("chris"),
            new NodeList<GraphQlLabel>(labels ?? []),
            new NodeList<GraphQlReview>(reviews ?? []),
            new NodeList<GraphQlThread>(threads ?? []),
            new NodeList<GraphQlCommitNode>(
                [new GraphQlCommitNode(new GraphQlCommit(new GraphQlRollup(new NodeList<GraphQlContext>(checks ?? []))))]
            )
        );

    private static GraphQlThread Thread(string author, bool resolved) =>
        new(resolved, new NodeList<GraphQlComment>([new GraphQlComment(new GraphQlActor(author))]));

    [Fact]
    public void Counts_only_unresolved_threads_and_keys_them_by_the_first_comment_author()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(threads: [Thread("coderabbitai", false), Thread("coderabbitai", false), Thread("coderabbitai", true), Thread("chris", false)])
        );

        _ = facts.UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(2);
        _ = facts.UnresolvedThreadsByAuthor["chris"].Should().Be(1);
    }

    [Fact]
    public void Treats_a_resolved_thread_as_participation_so_a_reviewer_that_ran_clean_is_not_pending()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(threads: [Thread("gitar-app", true)]));

        _ = facts.UnresolvedThreadsByAuthor.Should().NotContainKey("gitar-app");
        _ = facts.ParticipatingAuthors.Should().Contain("gitar-app");
    }

    [Fact]
    public void Records_a_review_author_as_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(reviews: [new GraphQlReview(new GraphQlActor("gitar-app"))]));

        _ = facts.ParticipatingAuthors.Should().Contain("gitar-app");
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
                    new GraphQlContext("CodeQL", "SUCCESS", new GraphQlCheckSuite(new GraphQlApp("github-code-scanning"))),
                    new GraphQlContext("flaky", "FAILURE", new GraphQlCheckSuite(new GraphQlApp("some-app"))),
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
    public void Matches_logins_case_insensitively_so_config_casing_cannot_silently_miss()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(threads: [Thread("CodeRabbitAI", false)]));

        _ = facts.UnresolvedThreadsByAuthor.ContainsKey("coderabbitai").Should().BeTrue();
    }
}

public class GitHubReviewFactsTransportTests
{
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        var facts = await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _ = handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/graphql");
        _ = facts[181].Labels.Should().Contain("review-high");
        _ = facts[181].UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(1);
    }

    [Fact]
    public async Task Throws_when_graphql_reports_errors_in_a_200_response()
    {
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([Json("""{"data":null,"errors":[{"message":"Could not resolve to a Repository"}]}""")])
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        var act = async () => await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }
}
