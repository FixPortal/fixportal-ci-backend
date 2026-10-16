using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubOptionsTests
{
    [Fact]
    public void Json_serialization_omits_the_token_and_preserves_public_options()
    {
        const string token = "github_pat_CIB12_json_sentinel";
        var options = new GitHubOptions { Owner = "FixPortal", Token = token };

        var serialized = JsonSerializer.Serialize(options);
        using var document = JsonDocument.Parse(serialized);
        var properties = document.RootElement.EnumerateObject().ToArray();

        properties.Should().ContainSingle().Which.Name.Should().Be("Owner");
        document.RootElement.GetProperty("Owner").GetString().Should().Be("FixPortal");
        serialized.Should().NotContain(token);
    }

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
