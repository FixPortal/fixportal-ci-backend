using AwesomeAssertions;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Infrastructure;

public class ContainerBuildConfigurationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../..")
    );

    [Fact]
    public void Public_build_does_not_require_private_package_credentials()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        var dockerfile = File.ReadAllText(Path.Combine(RepositoryRoot, "Dockerfile"));
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.yml"));
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var packages = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        var nuget = File.ReadAllText(Path.Combine(RepositoryRoot, "nuget.config"));

        _ = workflow.Should().NotContain("github-packages-token");
        _ = workflow.Should().NotContain("GITHUB_PACKAGES_TOKEN");
        _ = dockerfile.Should().NotContain("github-packages-token");
        _ = dockerfile.Should().NotContain("GITHUB_PACKAGES_TOKEN");
        _ = compose.Should().NotContain("github-packages-token");
        _ = compose.Should().NotContain("GITHUB_PACKAGES_TOKEN");
        _ = props.Should().NotContain("FixPortal.CodeStyle");
        _ = packages.Should().NotContain("FixPortal.CodeStyle");
        _ = nuget.Should().NotContain("nuget.pkg.github.com/FixPortal");
    }
}
