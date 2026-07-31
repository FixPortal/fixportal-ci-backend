using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalContractTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static PullRequest Pr(IReadOnlyList<ReviewSignal>? signals = null) =>
        new(7, "Add widget", "alice", "https://github.com/FixPortal/repo/pull/7", false, Instant.FromUnixTimeSeconds(1000), signals);

    [Fact]
    public void Pull_request_defaults_to_no_review_signals()
    {
        _ = Pr().ReviewSignals.Should().BeNull();
    }

    [Fact]
    public void Review_signal_state_serializes_as_a_camel_case_string()
    {
        var json = JsonSerializer.Serialize(new ReviewSignal("CodeQL", ReviewSignalState.Outstanding, 2, null), Options);
        _ = json.Should().Contain("\"outstanding\"").And.Contain("\"count\":2");
    }

    [Fact]
    public void Absent_signals_are_omitted_from_the_wire_rather_than_sent_as_an_empty_array()
    {
        var json = JsonSerializer.Serialize(Pr(), Options);
        _ = json.Should().NotContain("reviewSignals");
    }
}
