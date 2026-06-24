using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class AdminOptionsValidationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Start(string adminKey) =>
        factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            _ = builder.UseSetting("Admin:AdminKey", adminKey);
            // No background polling in tests; ValidateOnStart still runs at host start.
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }).CreateClient();

    [Fact]
    public void A_set_but_implausibly_short_admin_key_is_rejected_at_startup()
    {
        var act = () => Start("short");   // 5 chars, below the 16-char floor
        _ = act.Should().Throw<OptionsValidationException>();
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
