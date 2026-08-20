using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// Guards the M2 fix: config-bound JobLanes must REPLACE, not be shadowed by, the
// compiled defaults. The .NET configuration binder appends bound collection items
// to a pre-populated list, so the defaults live in DefaultJobLanes (not the bound
// property) and consumers call GetEffectiveJobLanes().
public class DashboardOptionsTests
{
    [Fact]
    public void EffectiveJobLanes_falls_back_to_defaults_when_none_configured()
    {
        var options = new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 30 };

        _ = options.JobLanes.Should().BeEmpty();
        _ = options.GetEffectiveJobLanes().Should().BeEquivalentTo(DashboardOptions.DefaultJobLanes);
        _ = options.GetEffectiveJobLanes().Select(l => l.Key).Should().Equal("deploys", "packages");
    }

    [Fact]
    public void EffectiveJobLanes_dedupes_by_key_keeping_the_last_entry()
    {
        var options = new DashboardOptions
        {
            SnapshotPath = "s.json",
            RefreshSeconds = 30,
            JobLanes =
            [
                new JobLaneOptions
                {
                    Key = "deploys",
                    Label = "Old",
                    RefreshSeconds = 300,
                },
                new JobLaneOptions
                {
                    Key = "deploys",
                    Label = "New",
                    RefreshSeconds = 150,
                },
            ],
        };

        var deploys = options.GetEffectiveJobLanes().Single(l => l.Key == "deploys");
        _ = deploys.RefreshSeconds.Should().Be(150);
        _ = deploys.Label.Should().Be("New");
    }

    // The exact defect: the binder appends the configured lane after the compiled
    // default of the same Key, and a naive FirstOrDefault(Key) returns the dead
    // default. GetEffectiveJobLanes must resolve the CONFIGURED cadence (150s), not 300s.
    [Fact]
    public void Configured_lane_cadence_wins_over_the_compiled_default_after_binding()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Dashboard:SnapshotPath"] = "s.json",
                    ["Dashboard:RefreshSeconds"] = "30",
                    ["Dashboard:JobLanes:0:Key"] = "deploys",
                    ["Dashboard:JobLanes:0:Label"] = "Deploys",
                    ["Dashboard:JobLanes:0:RefreshSeconds"] = "150",
                }
            )
            .Build();

        var options = new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 30 };
        config.GetSection("Dashboard").Bind(options);

        var deploys = options.GetEffectiveJobLanes().Single(l => l.Key == "deploys");
        _ = deploys.RefreshSeconds.Should().Be(150);
    }
}
