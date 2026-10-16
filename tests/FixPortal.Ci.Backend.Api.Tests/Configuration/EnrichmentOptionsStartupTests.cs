using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

public sealed class EnrichmentOptionsStartupTests
{
    [Theory]
    [InlineData("MergeState")]
    [InlineData("ReviewSignals")]
    public async Task Invalid_bound_cadence_must_fail_host_start_without_explicit_options_resolution(string section)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{section}:RefreshSeconds"] = "0" }
        );
        MergeStateOptions.AddMergeStateOptions(builder.Services, builder.Configuration);
        builder.Services.AddReviewSignalsOptions(builder.Configuration);
        using var host = builder.Build();

        var start = () => host.StartAsync(TestContext.Current.CancellationToken);

        await start.Should().ThrowAsync<OptionsValidationException>().WithMessage($"*{section}:RefreshSeconds*");
    }
}
