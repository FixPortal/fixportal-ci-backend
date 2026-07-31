using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalContractTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
}

public class ReviewSignalEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(DashboardSnapshot snapshot) =>
        factory
            .WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.ConfigureServices(services =>
                {
                    _ = services.RemoveAll<IHostedService>();
                    _ = services.RemoveAll<DashboardSnapshotState>();
                    var state = new DashboardSnapshotState();
                    state.Update(snapshot, DashboardSnapshotState.ComputePublicSnapshot(snapshot, snapshot.PublicCiTrend));
                    _ = services.AddSingleton(state);
                });
            })
            .CreateClient();

    [Fact]
    public async Task Get_snapshot_should_serialize_a_pull_request_without_review_signals_as_null()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "repo",
                    "https://github.com/FixPortal/repo",
                    false,
                    [],
                    [new PullRequest(7, "Add widget", "alice", "https://github.com/FixPortal/repo/pull/7", false, Instant.FromUnixTimeSeconds(1000))],
                    null,
                    [],
                    []
                ),
            ],
            [],
            null
        );
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var pr = doc.RootElement.GetProperty("repositories")[0].GetProperty("pullRequests")[0];
        _ = pr.TryGetProperty("reviewSignals", out var reviewSignals).Should().BeTrue();
        _ = reviewSignals.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
