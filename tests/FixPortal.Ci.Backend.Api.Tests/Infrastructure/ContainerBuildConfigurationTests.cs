using AwesomeAssertions;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Infrastructure;

public class ContainerBuildConfigurationTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Private_package_token_is_mounted_only_for_container_restore()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot, "Dockerfile"));

        _ = workflow.Should().Contain("github-packages-token=${{ secrets.GITHUB_TOKEN }}");
        _ = dockerfile.Should().Contain("--mount=type=secret,id=github-packages-token");
        _ = dockerfile.Should().Contain("GITHUB_PACKAGES_TOKEN=\"$(cat /run/secrets/github-packages-token)\"");
    }
}
