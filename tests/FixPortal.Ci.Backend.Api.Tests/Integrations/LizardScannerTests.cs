using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class LizardScannerTests
{
    [Fact]
    public void BuildCloneCommand_keeps_the_token_out_of_git_arguments()
    {
        var (arguments, environment) = LizardScanner.BuildCloneCommand("FixPortal", "fixportal-ci-dashboard", "super-secret-token", "C:\\temp\\repo");

        _ = string.Join(' ', arguments).Should().NotContain("super-secret-token");
        _ = arguments.Should().Contain("https://github.com/FixPortal/fixportal-ci-dashboard.git");
        _ = environment["GIT_CONFIG_KEY_0"].Should().Be("http.https://github.com/.extraheader");
        _ = environment["GIT_CONFIG_VALUE_0"].Should().Be($"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:super-secret-token"))}");
    }
}
