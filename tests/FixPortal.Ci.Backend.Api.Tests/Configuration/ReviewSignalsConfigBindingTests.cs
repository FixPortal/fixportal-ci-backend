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

    private static Dictionary<string, string> Reviewer(
        string name,
        string? botLogin,
        string? source = null,
        bool? commentsCountAsParticipation = null
    )
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
        if (commentsCountAsParticipation is { } flag)
        {
            settings["ReviewSignals:Reviewers:0:CommentsCountAsParticipation"] = flag ? "true" : "false";
        }
        return settings;
    }

    [Fact]
    public void The_shipped_defaults_are_the_deployed_ones()
    {
        // Production sets neither key, so these compiled-in defaults ARE what runs.
        // This pins the values rather than an arithmetic budget argument: the previous
        // version of this test multiplied cadence by repo count by per-repo cost, which
        // the incremental design made obsolete — a sweep now spends GraphQL points only
        // on pull requests whose watermark actually moved.
        using var provider = Provider(new Dictionary<string, string>());

        var options = provider.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value;

        _ = options.RefreshSeconds.Should().Be(150);
        // The backstop must survive on the default path. A zero here would silently
        // restore drain-to-empty, which is the failure this whole change exists to end.
        _ = options.ReserveBudgetPoints.Should().Be(1000);
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

    [Fact]
    public void Comment_participation_binds_from_the_env_var_shape()
    {
        using var provider = Provider(
            Reviewer(name: "Gitar", botLogin: "gitar-bot", commentsCountAsParticipation: true)
        );

        var options = provider.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value;

        _ = options.Reviewers[0].CommentsCountAsParticipation.Should().BeTrue();
    }

    [Fact]
    public void Comment_participation_without_a_bot_login_is_rejected_at_startup()
    {
        // The flag matches comments BY BotLogin. Set without one it can never match, and
        // the reviewer would report Pending forever -- the same silent misconfiguration
        // the ReviewThreads BotLogin rule exists to prevent.
        using var provider = Provider(
            Reviewer(
                name: "Mystery",
                botLogin: null,
                source: nameof(ReviewerSource.CodeScanning),
                commentsCountAsParticipation: true
            )
        );

        _ = provider
            .Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value)
            .Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*CommentsCountAsParticipation*");
    }
}
