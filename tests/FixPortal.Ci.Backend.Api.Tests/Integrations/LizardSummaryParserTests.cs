using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class LizardSummaryParserTests
{
    private const string Typical = """
        ================================================
        NLOC    CCN   token  PARAM  length  location
        ------------------------------------------------
            12     2     80      1      18   foo@1-18@./a.cs
        1 file analyzed.
        ==============================================================================================
        Total nloc   Avg.NLOC  AvgCCN  Avg.token  Fun Cnt  Warning cnt   Fun Rt   nloc Rt
        ----------------------------------------------------------------------------------------------
              12345      8.5     2.3      45.6        456         3      0.01      0.05
        """;

    [Fact]
    public void Parses_summary_row()
    {
        var m = LizardScanner.ParseLizardSummary(Typical, Instant.FromUnixTimeSeconds(42));
        _ = m.Should().NotBeNull();
        _ = m.Nloc.Should().Be(12345);
        _ = m.AvgComplexity.Should().BeApproximately(2.3, 0.001);
        _ = m.FunctionCount.Should().Be(456);
        _ = m.HighComplexityCount.Should().Be(3);
        _ = m.ComputedAt.Should().Be(Instant.FromUnixTimeSeconds(42));
    }

    [Fact]
    public void Returns_null_when_no_summary_table()
    {
        _ = LizardScanner.ParseLizardSummary("no table here", Instant.MinValue).Should().BeNull();
    }

    [Fact]
    public void Parses_all_zero_empty_repo()
    {
        var empty = """
            ==============================================================================================
            Total nloc   Avg.NLOC  AvgCCN  Avg.token  Fun Cnt  Warning cnt   Fun Rt   nloc Rt
            ----------------------------------------------------------------------------------------------
                    0      0.0      0.0       0.0          0         0      0.00      0.00
            """;
        var m = LizardScanner.ParseLizardSummary(empty, Instant.MinValue);
        _ = m.Should().NotBeNull();
        _ = m.Nloc.Should().Be(0);
        _ = m.FunctionCount.Should().Be(0);
    }
}
