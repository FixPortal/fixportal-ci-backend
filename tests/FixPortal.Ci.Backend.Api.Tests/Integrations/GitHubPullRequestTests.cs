using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubPullRequestTests
{
    [Fact]
    public void ToPullRequest_maps_fields()
    {
        var dto = new GitHubPullDto(
            7,
            "Fix bug",
            new GitHubUserDto("alice"),
            "https://github.com/FixPortal/repo/pull/7",
            true,
            Instant.FromUnixTimeSeconds(1000)
        );
        var pr = GitHubOrgClient.ToPullRequest(dto, "FixPortal", "repo");
        _ = pr.Number.Should().Be(7);
        _ = pr.Title.Should().Be("Fix bug");
        _ = pr.Author.Should().Be("alice");
        _ = pr.HtmlUrl.Should().Be("https://github.com/FixPortal/repo/pull/7");
        _ = pr.IsDraft.Should().BeTrue();
    }

    [Fact]
    public void ToPullRequest_applies_fallbacks_for_null_fields()
    {
        var dto = new GitHubPullDto(9, null, null, null, false, Instant.FromUnixTimeSeconds(1));
        var pr = GitHubOrgClient.ToPullRequest(dto, "FixPortal", "repo");
        _ = pr.Title.Should().Be("#9");
        _ = pr.Author.Should().Be("unknown");
        _ = pr.HtmlUrl.Should().Be("https://github.com/FixPortal/repo/pull/9");
    }
}
