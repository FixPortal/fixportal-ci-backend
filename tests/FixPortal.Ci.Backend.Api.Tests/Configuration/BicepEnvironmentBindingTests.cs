using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Ide;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

/// <summary>
/// G15 / G16 (test audit 2026-10-11): the Container App is configured entirely through the
/// environment variables declared in <c>deploy/bicep/main.bicep</c>, and a name that does not match
/// the option it is meant to bind is silently ignored, so the app starts and misbehaves (a wrong
/// <c>Admin__AdminKey</c> name fails closed with nothing to say why; a wrong reviewer property name
/// drops a reviewer's flag). The existing tests type their own configuration keys, which proves the
/// binder works and says nothing about what the template actually ships.
///
/// These read the REAL template, install its names in uniquely scoped process environment
/// variables and use the actual environment-variable provider, then bind through the real
/// options types, so every shipped name must reach a real property and the shipped reviewer roster
/// must come out as declared. The set of declared names is pinned in both directions, so a variable
/// added to the template without an option (or the reverse) is noticed.
/// </summary>
public class BicepEnvironmentBindingTests
{
    // Section name -> the options type the application binds it to. Pinned against Program.cs and
    // ReviewSignalsOptions.cs below, so this map cannot drift from what production actually binds.
    private static readonly Dictionary<string, Type> SectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GitHub"] = typeof(GitHubOptions),
        ["GitHubApp"] = typeof(GitHubAppOptions),
        ["Admin"] = typeof(AdminOptions),
        ["IdeIntegration"] = typeof(IdeIntegrationOptions),
        ["Dashboard"] = typeof(DashboardOptions),
        ["ReviewSignals"] = typeof(ReviewSignalsOptions),
    };

    private sealed record EnvEntry(string Name, string? Literal);

    [Fact]
    public void Every_section_the_template_configures_is_bound_by_the_application_to_the_expected_options_type()
    {
        var sources =
            File.ReadAllText(Path.Join(RepoRoot(), "src", "FixPortal.Ci.Backend.Api", "Program.cs"))
            + File.ReadAllText(
                Path.Join(
                    RepoRoot(),
                    "src",
                    "FixPortal.Ci.Backend.Api",
                    "Dashboard",
                    "Configuration",
                    "ReviewSignalsOptions.cs"
                )
            );

        foreach (var section in SectionTypes.Keys)
        {
            sources.Should().Contain($"GetSection(\"{section}\")", $"the application must bind the {section} section");
        }

        sources.Should().Contain("GetSection(\"Cors:AllowedOrigins\")");
    }

    [Fact]
    public void Every_environment_variable_the_template_declares_binds_to_a_real_option_property()
    {
        var entries = ReadEnvEntries();
        entries.Should().NotBeEmpty("the parser found no environment entries, so it and the template have drifted");

        using var environment = new TemplateEnvironment(entries.ToDictionary(e => e.Name, string? (e) => SampleFor(e)));
        var configuration = environment.Configuration;

        foreach (var entry in entries.Where(e => !e.Name.StartsWith("ReviewSignals__", StringComparison.Ordinal)))
        {
            var parts = entry.Name.Split("__");
            parts.Should().HaveCount(2, $"{entry.Name} should be Section__Property");
            SectionTypes.Should().ContainKey(parts[0], $"{entry.Name} names a section the application does not bind");

            var type = SectionTypes[parts[0]];
            var bound = configuration.GetSection(parts[0]).Get(type);
            bound.Should().NotBeNull();
            var property = type.GetProperty(
                parts[1],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            );
            property.Should().NotBeNull($"{entry.Name} must name a public property of {type.Name}");

            var expected = SampleFor(entry);
            Convert
                .ToString(property!.GetValue(bound), CultureInfo.InvariantCulture)
                .Should()
                .BeEquivalentTo(expected, $"{entry.Name} must reach {type.Name}.{property.Name}");
        }
    }

    [Fact]
    public void The_shipped_reviewer_roster_binds_with_every_declared_value_at_its_index()
    {
        var entries = ReadEnvEntries()
            .Where(e => e.Name.StartsWith("ReviewSignals__Reviewers__", StringComparison.Ordinal))
            .ToList();
        entries.Should().NotBeEmpty("the template declares a reviewer roster");

        using var environment = new TemplateEnvironment(entries.ToDictionary(e => e.Name, e => e.Literal));
        var configuration = environment.Configuration;
        var options = configuration.GetSection("ReviewSignals").Get<ReviewSignalsOptions>();
        options.Should().NotBeNull();

        var indexes = entries
            .Select(e => int.Parse(e.Name.Split("__")[2], CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToList();
        indexes.Should().Equal(Enumerable.Range(0, indexes.Count), "reviewer indexes must be contiguous from 0");
        options!.Reviewers.Should().HaveCount(indexes.Count);

        // The roster the estate ships, pinned by name and by the flags its behaviour depends on. A
        // reviewer re-indexed, renamed or stripped of a flag in the template changes what the board
        // waits on, so it should take a deliberate edit here too. Every reviewer must have a name:
        // an index that carries only flags would otherwise bind as a nameless reviewer.
        options
            .Reviewers.Select(r => r.Name)
            .Should()
            .Equal("CodeRabbit", "Gitar", "CodeQL", "Code Quality", "Secret Scanning");
        options.Reviewers.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Name));
        options.Reviewers[0].RequiredLabel.Should().Be("review-high");
        options.Reviewers[0].WaivedLabel.Should().Be("review-waived");
        options.Reviewers[0].WaivedBodyDirective.Should().Be("@coderabbitai ignore");
        options.Reviewers[1].CommentsCountAsParticipation.Should().BeTrue();
        options.Reviewers[2].Source.Should().Be(ReviewerSource.CodeScanning);
        options.Reviewers[3].BotLogin.Should().Be("github-code-quality");
        options.Reviewers[3].CodeScanningCheckCountsAsParticipation.Should().BeTrue();
        options.Reviewers[4].Source.Should().Be(ReviewerSource.SecretScanning);
        options.Reviewers.Skip(2).Should().OnlyContain(r => r.PublicOnly);

        foreach (var entry in entries)
        {
            var parts = entry.Name.Split("__"); // ReviewSignals, Reviewers, N, Property
            var reviewer = options.Reviewers[int.Parse(parts[2], CultureInfo.InvariantCulture)];
            var property = typeof(ReviewerOptions).GetProperty(
                parts[3],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
            );
            property.Should().NotBeNull($"{entry.Name} must name a ReviewerOptions property");

            Convert
                .ToString(property!.GetValue(reviewer), CultureInfo.InvariantCulture)
                .Should()
                .BeEquivalentTo(entry.Literal, $"{entry.Name} must bind to ReviewerOptions.{property.Name}");
        }
    }

    [Fact]
    public void The_template_still_declares_the_cors_origins_as_indexed_entries_the_application_reads()
    {
        File.ReadAllText(MainBicep()).Should().Contain("Cors__AllowedOrigins__${i}");
    }

    private static List<EnvEntry> ReadEnvEntries()
    {
        var text = File.ReadAllText(MainBicep());
        var matches = Regex.Matches(
            text,
            @"name: '(?<name>[A-Za-z0-9]+(?:__[A-Za-z0-9]+)+)'\s*\r?\n\s*(?<kind>value|secretRef): (?<rest>[^\r\n]+)"
        );
        return matches
            .Select(m =>
            {
                var rest = m.Groups["rest"].Value.Trim();
                string? literal =
                    m.Groups["kind"].Value == "value" && rest.StartsWith('\'') && rest.EndsWith('\'')
                        ? rest[1..^1]
                        : null;
                return new EnvEntry(m.Groups["name"].Value, literal);
            })
            .ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Environment_bound_shipped_roster_runs_real_startup_validation(bool invalidName)
    {
        var values = ReadEnvEntries()
            .Where(e => e.Name.StartsWith("ReviewSignals__", StringComparison.Ordinal))
            .ToDictionary(e => e.Name, e => e.Literal);
        if (invalidName)
        {
            values["ReviewSignals__Reviewers__0__Name"] = " ";
        }
        using var environment = new TemplateEnvironment(values);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(environment.Configuration);
        builder.Services.AddReviewSignalsOptions(builder.Configuration);
        using var host = builder.Build();
        if (invalidName)
        {
            var start = () => host.StartAsync(TestContext.Current.CancellationToken);
            await start.Should().ThrowAsync<OptionsValidationException>().WithMessage("*non-blank Name*");
        }
        else
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            host.Services.GetRequiredService<IOptions<ReviewSignalsOptions>>()
                .Value.Reviewers.Select(r => r.Name)
                .Should()
                .Equal("CodeRabbit", "Gitar", "CodeQL", "Code Quality", "Secret Scanning");
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Indexed_cors_template_names_bind_through_the_environment_provider()
    {
        var match = Regex.Match(
            File.ReadAllText(MainBicep()),
            @"map\(range\(0, length\(corsAllowedOrigins\)\), i => \{\s*name: '(?<name>[^']+)'\s*value: corsAllowedOrigins\[i\]\s*\}\)"
        );
        match.Success.Should().BeTrue("the actual indexed template expression must be understood before expansion");
        string[] origins = ["https://one.example.test", "https://two.example.test"];
        var values = origins
            .Select(
                (origin, index) =>
                    new
                    {
                        Name = match
                            .Groups["name"]
                            .Value.Replace(
                                "${i}",
                                index.ToString(CultureInfo.InvariantCulture),
                                StringComparison.Ordinal
                            ),
                        Origin = origin,
                    }
            )
            .ToDictionary(entry => entry.Name, string? (entry) => entry.Origin);
        using var environment = new TemplateEnvironment(values);
        environment.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>().Should().Equal(origins);
    }

    private sealed class TemplateEnvironment : IDisposable
    {
        private readonly List<string> _ownedNames = [];
        public ConfigurationRoot Configuration { get; }

        public TemplateEnvironment(IReadOnlyDictionary<string, string?> values)
        {
            var prefix = "AUDIT_BICEP_" + Guid.NewGuid().ToString("N") + "_";
            try
            {
                foreach (var (name, value) in values)
                {
                    var owned = prefix + name;
                    Environment
                        .GetEnvironmentVariable(owned)
                        .Should()
                        .BeNull("each scope owns only previously absent keys");
                    Environment.SetEnvironmentVariable(owned, value);
                    _ownedNames.Add(owned);
                }
                Configuration = (ConfigurationRoot)new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            }
            catch
            {
                ClearOwnedNames();
                throw;
            }
        }

        public void Dispose()
        {
            Configuration.Dispose();
            ClearOwnedNames();
        }

        private void ClearOwnedNames()
        {
            foreach (var name in _ownedNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    // The template's own literal where it has one; otherwise a sample of the right type, because a
    // value taken from a Bicep parameter or a secret is only known at deploy time.
    private static string SampleFor(EnvEntry entry)
    {
        if (entry.Literal is not null)
        {
            return entry.Literal;
        }

        var parts = entry.Name.Split("__");
        var type = SectionTypes.GetValueOrDefault(parts[0]);
        var property = type?.GetProperty(
            parts[^1],
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        );
        var propertyType =
            Nullable.GetUnderlyingType(property?.PropertyType ?? typeof(string)) ?? property?.PropertyType;
        if (propertyType == typeof(int) || propertyType == typeof(long))
        {
            return "4242";
        }

        return propertyType == typeof(bool) ? "True" : $"bound-{entry.Name}";
    }

    private static string MainBicep() => Path.Join(RepoRoot(), "deploy", "bicep", "main.bicep");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "FixPortal.Ci.Backend.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("FixPortal.Ci.Backend.slnx not found above the test binaries.");
    }
}
