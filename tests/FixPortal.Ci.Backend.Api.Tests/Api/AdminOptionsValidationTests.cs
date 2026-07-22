using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class AdminOptionsValidationTests
{
    private static void Start(string adminKey)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory
            .WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.UseSetting("GitHub:Owner", "FixPortal");
                _ = builder.UseSetting("Admin:AdminKey", adminKey);
                // No background polling in tests; ValidateOnStart still runs at host start.
                _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            })
            .CreateClient();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("short", false)]
    [InlineData("123456789012345", false)]
    [InlineData("1234567890123456", true)]
    public void Admin_key_length_rule_accepts_only_empty_or_sufficiently_long_values(string adminKey, bool expected)
    {
        var options = new AdminOptions { AdminKey = adminKey };

        _ = options.HasValidAdminKeyLength().Should().Be(expected);
    }

    [Fact]
    public void A_set_but_implausibly_short_admin_key_is_rejected_at_startup()
    {
        var act = () => Start("short"); // 5 chars, below the 16-char floor
        _ = act.Should().Throw<Exception>();
    }

    [Fact]
    public void An_empty_admin_key_is_allowed_fails_closed_at_the_endpoint()
    {
        // Empty is the fail-closed default: the admin endpoint returns 401 when no
        // key is configured, so an empty key must not block startup.
        var act = () => Start("");
        _ = act.Should().NotThrow();
    }

    [Fact]
    public void A_sufficiently_long_admin_key_is_allowed()
    {
        var act = () => Start("a-perfectly-fine-admin-key");
        _ = act.Should().NotThrow();
    }
}
