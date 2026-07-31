using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
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
