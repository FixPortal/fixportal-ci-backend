using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

public class RepositoryFiltersConfigBindingTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Configured_repository_filters_replace_the_empty_defaults()
    {
        using var f = ConfigureFactory(
            new Dictionary<string, string>
            {
                ["Dashboard:IncludeRepositories:0"] = "api-*",
                ["Dashboard:ExcludeRepositories:0"] = "api-legacy",
                ["Dashboard:IncludeTopics:0"] = "backend",
                ["Dashboard:ExcludeTopics:0"] = "internal",
            }
        );

        var options = f.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;

        _ = options.IncludeRepositories.Should().Equal("api-*");
        _ = options.ExcludeRepositories.Should().Equal("api-legacy");
        _ = options.IncludeTopics.Should().Equal("backend");
        _ = options.ExcludeTopics.Should().Equal("internal");
    }

    [Theory]
    [InlineData("IncludeRepositories")]
    [InlineData("ExcludeRepositories")]
    [InlineData("IncludeTopics")]
    [InlineData("ExcludeTopics")]
    public void Blank_repository_filter_patterns_are_rejected_at_startup(string listName)
    {
        using var f = ConfigureFactory(new Dictionary<string, string> { [$"Dashboard:{listName}:0"] = " " });

        var act = () => f.CreateClient();

        _ = act.Should().Throw<Exception>().WithMessage($"*Dashboard:{listName}*");
    }

    private WebApplicationFactory<Program> ConfigureFactory(IReadOnlyDictionary<string, string> settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            foreach (var (key, value) in settings)
            {
                _ = builder.UseSetting(key, value);
            }
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
}
