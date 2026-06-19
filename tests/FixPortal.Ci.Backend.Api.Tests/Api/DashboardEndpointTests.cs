using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class DashboardEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(DashboardSnapshot? seed) =>
        factory.WithWebHostBuilder(builder =>
        {
            // Satisfy GitHub token ValidateOnStart so the test host can start.
            _ = builder.UseSetting("GitHub:Token", "test-token");
            _ = builder.ConfigureServices(services =>
            {
                // No background polling in tests; seed the in-memory holder the
                // endpoint reads from.
                _ = services.RemoveAll<IHostedService>();
                _ = services.RemoveAll<DashboardSnapshotState>();
                var state = new DashboardSnapshotState();
                if (seed is not null)
                {
                    state.Update(seed, DashboardSnapshotState.ComputePublicSnapshot(seed));
                }
                _ = services.AddSingleton(state);
            });
        }).CreateClient();

    [Fact]
    public async Task Get_snapshot_should_return_latest_dashboard_snapshot()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 5, 28, 18, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "repo",
                    "https://github.com/FixPortal/repo",
                    true,
                    [
                        new WorkflowSnapshot(
                            "CI",
                            "ci.yml",
                            SignalState.Success,
                            new WorkflowRun("completed", "success", "https://x", "t", 1, "main", "push", Instant.FromUtc(2026, 5, 28, 17, 0)))
                    ],
                    [],
                    null,
                    [],
                    [])
            ],
            [new SummaryCount("success", 1)],
            null);
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Get_snapshot_should_return_no_content_before_first_refresh()
    {
        var client = CreateClient(seed: null);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_api_route_should_return_404_not_the_spa_shell()
    {
        var client = CreateClient(seed: null);

        var response = await client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_snapshot_should_exclude_private_repositories_from_response()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot("public-repo", "https://github.com/FixPortal/public-repo", false, [], [], null, [], []),
                new RepositorySnapshot("private-repo", "https://github.com/FixPortal/private-repo", true, [], [], null, [], [])
            ],
            [],
            null);
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var repos = doc.RootElement.GetProperty("repositories");
        _ = repos.GetArrayLength().Should().Be(1);
        _ = repos[0].GetProperty("name").GetString().Should().Be("public-repo");
    }

    [Fact]
    public async Task Get_snapshot_should_return_empty_repositories_when_all_are_private()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot("private-repo-1", "https://github.com/FixPortal/private-repo-1", true, [], [], null, [], []),
                new RepositorySnapshot("private-repo-2", "https://github.com/FixPortal/private-repo-2", true, [], [], null, [], [])
            ],
            [],
            null);
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        _ = doc.RootElement.GetProperty("repositories").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Get_snapshot_should_return_all_repositories_when_all_are_public()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot("public-repo-1", "https://github.com/FixPortal/public-repo-1", false, [], [], null, [], []),
                new RepositorySnapshot("public-repo-2", "https://github.com/FixPortal/public-repo-2", false, [], [], null, [], [])
            ],
            [],
            null);
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var repos = doc.RootElement.GetProperty("repositories");
        _ = repos.GetArrayLength().Should().Be(2);
        _ = repos[0].GetProperty("name").GetString().Should().Be("public-repo-1");
        _ = repos[1].GetProperty("name").GetString().Should().Be("public-repo-2");
    }

    [Fact]
    public async Task Snapshot_should_allow_cross_origin_get_from_configured_origin()
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            _ = b.UseSetting("GitHub:Token", "test-token");
            _ = b.UseSetting("Cors:AllowedOrigins:0", "https://app.fixportal.org");
            _ = b.ConfigureServices(s =>
            {
                _ = s.RemoveAll<IHostedService>();
                _ = s.RemoveAll<DashboardSnapshotState>();
                _ = s.AddSingleton(new DashboardSnapshotState());
            });
        }).CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot");
        request.Headers.Add("Origin", "https://app.fixportal.org");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().Contain("https://app.fixportal.org");
    }

    [Fact]
    public async Task Snapshot_should_not_emit_cors_header_for_unconfigured_origin()
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            _ = b.UseSetting("GitHub:Token", "test-token");
            _ = b.UseSetting("Cors:AllowedOrigins:0", "https://app.fixportal.org");
            _ = b.ConfigureServices(s =>
            {
                _ = s.RemoveAll<IHostedService>();
                _ = s.RemoveAll<DashboardSnapshotState>();
                _ = s.AddSingleton(new DashboardSnapshotState());
            });
        }).CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot");
        request.Headers.Add("Origin", "https://evil.example.com");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
