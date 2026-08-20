using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubOptionsTests
{
    [Fact]
    public void ToString_redacts_token()
    {
        const string token = "github_pat_CIB12_sentinel";
        var options = new GitHubOptions { Owner = "FixPortal", Token = token };

        var text = options.ToString();

        _ = text.Should().Contain("FixPortal");
        _ = text.Should().Contain("Token = ***");
        _ = text.Should().NotContain(token);
    }
}
