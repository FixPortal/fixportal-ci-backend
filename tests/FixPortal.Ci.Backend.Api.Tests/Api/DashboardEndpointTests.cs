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
using NodaTime.Text;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public class DashboardEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient(DashboardSnapshot? seed) => CreateClient(seed, adminKey: null);

    private HttpClient CreateClient(DashboardSnapshot? seed, string? adminKey) =>
        factory
            .WithWebHostBuilder(builder =>
            {
                // Satisfy GitHub token ValidateOnStart so the test host can start.
                _ = builder.UseSetting("GitHub:Token", "test-token");
                if (adminKey is not null)
                {
                    _ = builder.UseSetting("Admin:AdminKey", adminKey);
                }
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
            })
            .CreateClient();

    [Fact]
    public async Task Get_snapshot_should_return_latest_dashboard_snapshot()
    {
        var refreshedAt = Instant.FromUtc(2026, 5, 28, 18, 0);
        var snapshot = new DashboardSnapshot(
            refreshedAt,
            "FixPortal",
            [
                new RepositorySnapshot(
                    "repo",
                    "https://github.com/FixPortal/repo",
                    false, // public: the endpoint under test strips private repos, so a
                    // private seed here would make repositories[0] never exist.
                    [
                        new WorkflowSnapshot(
                            "CI",
                            "ci.yml",
                            SignalState.Success,
                            new WorkflowRun(
                                "completed",
                                "success",
                                "https://x",
                                "t",
                                1,
                                "main",
                                "push",
                                Instant.FromUtc(2026, 5, 28, 17, 0)
                            )
                        ),
                    ],
                    [],
                    null,
                    [],
                    []
                ),
            ],
            [new SummaryCount("success", 1)],
            null
        );
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        // §4: the original assertion here only checked status/content-type and would
        // have passed even for an empty or wrong snapshot returned as 200 JSON. Read
        // the body and pin it against the seed, as the sibling private-filter tests
        // in this file already do.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        _ = doc.RootElement.GetProperty("org").GetString().Should().Be("FixPortal");
        _ = doc
            .RootElement.GetProperty("refreshedAt")
            .GetString()
            .Should()
            .Be(InstantPattern.ExtendedIso.Format(refreshedAt));
        var repo0 = doc.RootElement.GetProperty("repositories")[0];
        _ = repo0.GetProperty("name").GetString().Should().Be("repo");
        _ = repo0.GetProperty("workflows")[0].GetProperty("state").GetString().Should().Be("success");
    }

    // CB-H7: the HTTP wire contract must serialize enums as camelCase strings (not
    // integers) and NodaTime Instants as parseable ISO-8601 instants — no test at
    // the HTTP layer read either before. Removing the JsonStringEnumConverter or
    // ConfigureForNodaTime in Program.cs would break every frontend consumer with
    // nothing here failing until now.
    [Fact]
    public async Task Get_snapshot_should_serialize_workflow_state_as_camelCase_enum_and_refreshedAt_as_an_instant()
    {
        var runInstant = Instant.FromUtc(2026, 5, 28, 17, 0);
        var refreshedAt = Instant.FromUtc(2026, 5, 28, 18, 0);
        var snapshot = new DashboardSnapshot(
            refreshedAt,
            "FixPortal",
            [
                new RepositorySnapshot(
                    "repo",
                    "https://github.com/FixPortal/repo",
                    false,
                    [
                        new WorkflowSnapshot(
                            "CI",
                            "ci.yml",
                            SignalState.Failure,
                            new WorkflowRun("completed", "failure", "https://x", "t", 1, "main", "push", runInstant)
                        ),
                    ],
                    [],
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

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var workflow = doc.RootElement.GetProperty("repositories")[0].GetProperty("workflows")[0];
        // The enum must serialize as the camelCase string "failure", never a bare integer.
        _ = workflow.GetProperty("state").ValueKind.Should().Be(JsonValueKind.String);
        _ = workflow.GetProperty("state").GetString().Should().Be("failure");
        // The Instant must round-trip as a parseable ISO-8601 instant, not a numeric
        // tick count and not throw during deserialization.
        var refreshedAtRaw = doc.RootElement.GetProperty("refreshedAt").GetString();
        var parsed = InstantPattern.ExtendedIso.Parse(refreshedAtRaw!);
        _ = parsed.Success.Should().BeTrue();
        _ = parsed.Value.Should().Be(refreshedAt);
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
                new RepositorySnapshot(
                    "public-repo",
                    "https://github.com/FixPortal/public-repo",
                    false,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "private-repo",
                    "https://github.com/FixPortal/private-repo",
                    true,
                    [],
                    [],
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
                new RepositorySnapshot(
                    "private-repo-1",
                    "https://github.com/FixPortal/private-repo-1",
                    true,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "private-repo-2",
                    "https://github.com/FixPortal/private-repo-2",
                    true,
                    [],
                    [],
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
        _ = doc.RootElement.GetProperty("repositories").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Get_snapshot_should_return_all_repositories_when_all_are_public()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "public-repo-1",
                    "https://github.com/FixPortal/public-repo-1",
                    false,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "public-repo-2",
                    "https://github.com/FixPortal/public-repo-2",
                    false,
                    [],
                    [],
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
        var repos = doc.RootElement.GetProperty("repositories");
        _ = repos.GetArrayLength().Should().Be(2);
        _ = repos[0].GetProperty("name").GetString().Should().Be("public-repo-1");
        _ = repos[1].GetProperty("name").GetString().Should().Be("public-repo-2");
    }

    [Fact]
    public async Task Snapshot_should_allow_cross_origin_get_from_configured_origin()
    {
        var client = factory
            .WithWebHostBuilder(b =>
            {
                _ = b.UseSetting("GitHub:Token", "test-token");
                _ = b.UseSetting("Cors:AllowedOrigins:0", "https://app.fixportal.org");
                _ = b.ConfigureServices(s =>
                {
                    _ = s.RemoveAll<IHostedService>();
                    _ = s.RemoveAll<DashboardSnapshotState>();
                    _ = s.AddSingleton(new DashboardSnapshotState());
                });
            })
            .CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot");
        request.Headers.Add("Origin", "https://app.fixportal.org");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("https://app.fixportal.org");
    }

    [Fact]
    public async Task Snapshot_should_not_emit_cors_header_for_unconfigured_origin()
    {
        var client = factory
            .WithWebHostBuilder(b =>
            {
                _ = b.UseSetting("GitHub:Token", "test-token");
                _ = b.UseSetting("Cors:AllowedOrigins:0", "https://app.fixportal.org");
                _ = b.ConfigureServices(s =>
                {
                    _ = s.RemoveAll<IHostedService>();
                    _ = s.RemoveAll<DashboardSnapshotState>();
                    _ = s.AddSingleton(new DashboardSnapshotState());
                });
            })
            .CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot");
        request.Headers.Add("Origin", "https://evil.example.com");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    // CB-C1: /api/dashboard/snapshot/admin is the only path that returns private
    // repos and the only shared-secret gate in the API. These four cases pin the
    // fail-closed contract: no header, a wrong key, and an unconfigured key must
    // all 401; only the correct key gets the full (private-inclusive) snapshot.
    private const string AdminKey = "a-valid-admin-key-1234";

    private static DashboardSnapshot SnapshotWithPrivateRepo() =>
        new(
            Instant.FromUtc(2026, 6, 1, 0, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "public-repo",
                    "https://github.com/FixPortal/public-repo",
                    false,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "private-repo",
                    "https://github.com/FixPortal/private-repo",
                    true,
                    [],
                    [],
                    null,
                    [],
                    []
                ),
            ],
            [],
            null
        );

    [Fact]
    public async Task Admin_snapshot_should_return_401_when_no_key_header_is_present()
    {
        var client = CreateClient(SnapshotWithPrivateRepo(), AdminKey);

        var response = await client.GetAsync("/api/dashboard/snapshot/admin", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_snapshot_should_return_401_when_the_key_is_wrong()
    {
        var client = CreateClient(SnapshotWithPrivateRepo(), AdminKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot/admin");
        request.Headers.Add("X-Admin-Key", "definitely-the-wrong-key");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_snapshot_should_return_401_when_no_admin_key_is_configured()
    {
        // Empty AdminKey is the fail-closed default (Admin:AdminKey unset) — the
        // endpoint must reject unconditionally, never fall through to Results.Ok.
        var client = CreateClient(SnapshotWithPrivateRepo(), adminKey: "");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot/admin");
        request.Headers.Add("X-Admin-Key", "anything-at-all");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_snapshot_should_return_200_with_private_repos_when_the_key_is_correct()
    {
        var client = CreateClient(SnapshotWithPrivateRepo(), AdminKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/snapshot/admin");
        request.Headers.Add("X-Admin-Key", AdminKey);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var names = doc
            .RootElement.GetProperty("repositories")
            .EnumerateArray()
            .Select(r => r.GetProperty("name").GetString())
            .ToList();
        _ = names.Should().Contain("public-repo");
        _ = names.Should().Contain("private-repo");
    }

    // CB-C2: /api/health is unauthenticated, so it must never echo state.LastAuthError
    // verbatim — that raw text embeds the failing GitHub request URL, which includes
    // the private repo name. Seed a sentinel private-repo name into LastAuthError and
    // assert it never reaches the response body.
    [Fact]
    public async Task Health_should_return_503_degraded_without_leaking_the_raw_auth_error()
    {
        const string sentinelPrivateRepoName = "SENTINEL-PRIVATE-REPO-MUST-NOT-LEAK";
        var client = factory
            .WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.ConfigureServices(services =>
                {
                    _ = services.RemoveAll<IHostedService>();
                    _ = services.RemoveAll<DashboardSnapshotState>();
                    var state = new DashboardSnapshotState();
                    state.SetAuthError(
                        $"GitHub authentication failed (HTTP 401 Unauthorized) for repos/FixPortal/{sentinelPrivateRepoName}/actions/workflows. Verify the configured PAT token."
                    );
                    _ = services.AddSingleton(state);
                });
            })
            .CreateClient();

        var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        _ = body.Should().NotContain(sentinelPrivateRepoName);
        using var doc = JsonDocument.Parse(body);
        _ = doc.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
    }

    [Fact]
    public async Task Health_should_return_200_healthy_when_no_auth_error_is_set()
    {
        var client = factory
            .WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.ConfigureServices(services =>
                {
                    _ = services.RemoveAll<IHostedService>();
                    _ = services.RemoveAll<DashboardSnapshotState>();
                    _ = services.AddSingleton(new DashboardSnapshotState());
                });
            })
            .CreateClient();

        var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        _ = doc.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
    }
}
