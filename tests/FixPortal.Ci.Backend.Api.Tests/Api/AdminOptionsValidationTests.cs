using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class AdminOptionsValidationTests
{
    private static void Start(string owner, string token, string adminKey = "", int? dashboardRefreshSeconds = null)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", token);
            _ = builder.UseSetting("GitHub:Owner", owner);
            _ = builder.UseSetting("Admin:AdminKey", adminKey);
            if (dashboardRefreshSeconds is not null)
            {
                _ = builder.UseSetting("Dashboard:RefreshSeconds", dashboardRefreshSeconds.Value.ToString());
            }
            // No background polling in tests; ValidateOnStart still runs at host start.
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();
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
        var act = () => Start("FixPortal", "test-token", "short"); // 5 chars, below the 16-char floor
        _ = act.Should().Throw<Exception>();
    }

    [Fact]
    public void An_empty_admin_key_is_allowed_fails_closed_at_the_endpoint()
    {
        // Empty is the fail-closed default: the admin endpoint returns 401 when no
        // key is configured, so an empty key must not block startup.
        var act = () => Start("FixPortal", "test-token");
        _ = act.Should().NotThrow();
    }

    [Fact]
    public void A_sufficiently_long_admin_key_is_allowed()
    {
        var act = () => Start("FixPortal", "test-token", "a-perfectly-fine-admin-key");
        _ = act.Should().NotThrow();
    }

    [Fact]
    public void A_non_positive_dashboard_refresh_cadence_is_rejected_at_startup()
    {
        var act = () => Start("FixPortal", "test-token", dashboardRefreshSeconds: 0);

        _ = act.Should().Throw<Exception>().WithMessage("*Dashboard:RefreshSeconds*");
    }

    [Theory]
    [InlineData("", "test-token", "GitHub:Owner")]
    [InlineData("FixPortal", "", "GitHub:Token")]
    public void A_blank_required_GitHub_setting_is_rejected_at_startup(
        string owner,
        string token,
        string missingSetting
    )
    {
        var act = () => Start(owner, token);

        _ = act.Should().Throw<Exception>().WithMessage($"*{missingSetting}*");
    }
}
