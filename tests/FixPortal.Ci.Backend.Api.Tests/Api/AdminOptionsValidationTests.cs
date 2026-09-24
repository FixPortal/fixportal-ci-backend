using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class AdminOptionsValidationTests
{
    private sealed class ValidationFactory(
        string owner,
        string token,
        string adminKey,
        int? dashboardSettingValue,
        string dashboardSettingName
    ) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.UseSetting("GitHub:Token", token);
            _ = builder.UseSetting("GitHub:Owner", owner);
            _ = builder.UseSetting("Admin:AdminKey", adminKey);
            if (dashboardSettingValue is not null)
            {
                _ = builder.UseSetting(dashboardSettingName, dashboardSettingValue.Value.ToString());
            }
            // No background polling in tests; ValidateOnStart still runs at host start.
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }

    private static void Start(
        string owner,
        string token,
        string adminKey = "",
        int? dashboardSettingValue = null,
        string dashboardSettingName = "Dashboard:RefreshSeconds"
    )
    {
        using var factory = new ValidationFactory(owner, token, adminKey, dashboardSettingValue, dashboardSettingName);
        using var client = factory.CreateClient();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("short", false)]
    [InlineData("123456789012345", false)]
    [InlineData("1234567890123456", true)]
    public void Admin_key_length_rule_accepts_only_empty_or_sufficiently_long_values(string? adminKey, bool expected)
    {
        // Null is an explicitly bound null, not "unset": it must fail validation with
        // the configured message rather than read as the fail-closed empty default.
        var options = new AdminOptions { AdminKey = adminKey! };

        _ = options.HasValidAdminKeyLength().Should().Be(expected);
    }

    [Fact]
    public void A_set_but_implausibly_short_admin_key_is_rejected_at_startup()
    {
        var act = () => Start("FixPortal", "test-token", "short"); // 5 chars, below the 16-char floor
        _ = act.Should().Throw<OptionsValidationException>().WithMessage("*Admin:AdminKey*");
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
        var act = () => Start("FixPortal", "test-token", dashboardSettingValue: 0);

        _ = act.Should().Throw<OptionsValidationException>().WithMessage("*Dashboard:RefreshSeconds*");
    }

    [Theory]
    [InlineData("Dashboard:MetricsRefreshSeconds", "*Dashboard:MetricsRefreshSeconds*")]
    [InlineData("Dashboard:MergedPrRefreshSeconds", "*Dashboard:MergedPrRefreshSeconds*")]
    [InlineData("Dashboard:JobLanes:0:RefreshSeconds", "*Dashboard:JobLanes:RefreshSeconds*")]
    [InlineData("Dashboard:JobLanes:0:MaxRunsToScan", "*Dashboard:JobLanes:MaxRunsToScan*")]
    public void Other_non_positive_dashboard_cadences_and_scan_limits_are_rejected_at_startup(
        string setting,
        string expectedMessage
    )
    {
        var act = () => Start("FixPortal", "test-token", dashboardSettingValue: 0, dashboardSettingName: setting);

        _ = act.Should().Throw<OptionsValidationException>().WithMessage(expectedMessage);
    }

    [Theory]
    [InlineData("", "test-token")]
    [InlineData("FixPortal", "")]
    public void A_blank_required_GitHub_setting_is_rejected_at_startup(string owner, string token)
    {
        var act = () => Start(owner, token);

        _ = act.Should().Throw<OptionsValidationException>().WithMessage("*GitHub:*");
    }
}
