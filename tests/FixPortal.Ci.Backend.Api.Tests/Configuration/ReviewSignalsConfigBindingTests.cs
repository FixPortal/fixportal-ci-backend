using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

// The shipped defaults, read through the real binder over the real appsettings.json.
// "Off by default" and "dependency PRs carry no pills" are promises made to anyone who
// upgrades a fork, and nothing else pins them: a stray reviewer entry in
// appsettings.json would turn the feature on for every self-hoster and start issuing
// GitHub requests they never asked for.
public class ReviewSignalsConfigBindingTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Shipped_appsettings_configures_no_reviewers_so_the_feature_is_off()
    {
        using var f = factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            _ = builder.ConfigureServices(s => s.RemoveAll<IHostedService>());
        });

        var options = f.Services.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value;

        _ = options.Reviewers.Should().BeEmpty();
        // Both spellings of each dependency bot: GraphQL reports a Bot node's login
        // without the "[bot]" suffix, which is a REST-ism, so a suffix-only list would
        // silently match nothing and put pills on every Dependabot PR.
        _ = options
            .ExcludedAuthors.Should()
            .BeEquivalentTo("dependabot", "dependabot[bot]", "renovate", "renovate[bot]");
    }
}

// ReviewSignalsOptions is validated at boot like its GitHub/Dashboard/Admin neighbours.
// It has to be: RefreshSeconds reaches a PeriodicTimer inside a BackgroundService, and
// BackgroundServiceExceptionBehavior defaults to StopHost, so a typo'd
// ReviewSignals__RefreshSeconds=0 would take the whole board down AFTER cold start
// rather than failing loudly at boot.
public class ReviewSignalsOptionsValidationTests
{
    private static ServiceProvider Provider(IReadOnlyDictionary<string, string> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddReviewSignalsOptions(configuration);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string> Reviewer(string name, string? botLogin, string? source = null)
    {
        var settings = new Dictionary<string, string> { ["ReviewSignals:Reviewers:0:Name"] = name };
        if (botLogin is not null)
        {
            settings["ReviewSignals:Reviewers:0:BotLogin"] = botLogin;
        }
        if (source is not null)
        {
            settings["ReviewSignals:Reviewers:0:Source"] = source;
        }
        return settings;
    }

    [Fact]
    public void A_non_positive_review_signal_cadence_is_rejected_at_startup()
    {
        using var provider = Provider(new Dictionary<string, string> { ["ReviewSignals:RefreshSeconds"] = "0" });

        _ = provider
            .Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value)
            .Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*ReviewSignals:RefreshSeconds*");
    }

    [Fact]
    public void A_reviewer_with_no_name_is_rejected_at_startup()
    {
        // `required string Name` is not honoured by the configuration binder, so without
        // this rule a Name-less entry binds happily and renders a nameless pill.
        using var provider = Provider(Reviewer(name: " ", botLogin: "gitar-app"));

        _ = provider
            .Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value)
            .Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*Name*");
    }

    [Fact]
    public void A_review_threads_reviewer_with_no_bot_login_is_rejected_at_startup()
    {
        using var provider = Provider(Reviewer(name: "Gitar", botLogin: null));

        _ = provider
            .Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value)
            .Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*BotLogin*");
    }

    [Fact]
    public void A_code_scanning_reviewer_needs_no_bot_login()
    {
        using var provider = Provider(
            Reviewer(name: "CodeQL", botLogin: null, source: nameof(ReviewerSource.CodeScanning))
        );

        _ = provider.Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value).Should().NotThrow();
    }
}
