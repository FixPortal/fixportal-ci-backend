using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

// M2 regression, exercised through the real config binder + shipped appsettings.json:
// the configured 150s lane cadence must be in force, not shadowed by the compiled
// 300s default. Before the fix the binder appended config to the pre-populated
// defaults and FirstOrDefault(Key) resolved the dead default.
public class JobLanesConfigBindingTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Shipped_appsettings_JobLanes_cadence_is_effective_not_shadowed()
    {
        using var f = factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            _ = builder.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });

        var options = f.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;

        _ = options.GetEffectiveJobLanes().Should().OnlyContain(l => l.RefreshSeconds == 150);
        _ = options.GetEffectiveJobLanes().Select(l => l.Key).Should().BeEquivalentTo("deploys", "packages");
    }
}
