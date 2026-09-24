using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

public sealed class MergeStateOptionsValidationTests
{
    [Fact]
    public void Non_positive_refresh_cadence_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MergeState:RefreshSeconds"] = "0" })
            .Build();
        var services = new ServiceCollection();
        MergeStateOptions.AddMergeStateOptions(services, configuration);
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<MergeStateOptions>>().Value;

        _ = act.Should().Throw<OptionsValidationException>().WithMessage("*MergeState:RefreshSeconds*");
    }
}
