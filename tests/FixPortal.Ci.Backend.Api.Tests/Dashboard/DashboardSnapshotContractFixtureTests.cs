using System.Net;
using System.Text.Json.Nodes;
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

public class DashboardSnapshotContractFixtureTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
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
                    state.Update(
                        snapshot,
                        DashboardSnapshotState.ComputePublicSnapshot(snapshot, snapshot.PublicCiTrend)
                    );
                    _ = services.AddSingleton(state);
                });
            })
            .CreateClient();

    [Fact]
    public async Task Get_snapshot_should_match_the_versioned_dashboard_contract_fixture()
    {
        var snapshot = new DashboardSnapshot(
            Instant.FromUtc(2026, 6, 1, 12, 30),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "public-repo",
                    "https://github.com/FixPortal/public-repo",
                    false,
                    [
                        new WorkflowSnapshot(
                            "CI",
                            "ci.yml",
                            SignalState.Success,
                            new WorkflowRun(
                                "completed",
                                "success",
                                "https://github.com/FixPortal/public-repo/actions/runs/101",
                                "CI #101",
                                101,
                                "main",
                                "push",
                                Instant.FromUtc(2026, 6, 1, 12, 0),
                                "FixPortal/public-repo",
                                ".github/workflows/ci.yml",
                                101,
                                2,
                                "abc123"
                            ),
                            [
                                new WorkflowRun(
                                    "completed",
                                    "success",
                                    "https://github.com/FixPortal/public-repo/actions/runs/101",
                                    "CI #101",
                                    101,
                                    "main",
                                    "push",
                                    Instant.FromUtc(2026, 6, 1, 12, 0),
                                    "FixPortal/public-repo",
                                    ".github/workflows/ci.yml",
                                    101,
                                    2,
                                    "abc123"
                                ),
                                new WorkflowRun(
                                    "completed",
                                    "failure",
                                    "https://github.com/FixPortal/public-repo/actions/runs/100",
                                    "CI #100",
                                    100,
                                    "feature/widget",
                                    "pull_request",
                                    Instant.FromUtc(2026, 6, 1, 11, 0),
                                    "FixPortal/public-repo",
                                    ".github/workflows/ci.yml",
                                    100,
                                    1,
                                    "def456"
                                ),
                            ]
                        ),
                    ],
                    [
                        new PullRequest(
                            42,
                            "Add dashboard contract fixture",
                            "alice",
                            "https://github.com/FixPortal/public-repo/pull/42",
                            false,
                            Instant.FromUtc(2026, 6, 1, 10, 0),
                            [
                                new ReviewSignal(
                                    "CodeRabbit",
                                    ReviewSignalState.Clean,
                                    0,
                                    "https://github.com/FixPortal/public-repo/pull/42#pullrequestreview-1"
                                ),
                                new ReviewSignal("CodeQL", ReviewSignalState.Outstanding, 2, null),
                                new ReviewSignal("Gitar", ReviewSignalState.Pending, null, null),
                                new ReviewSignal("Optional", ReviewSignalState.Disabled, null, null),
                            ],
                            false,
                            "0beec7b5ea3f0fdbc95d0dd47f3c5bc275da8a33"
                        ),
                    ],
                    new RepoMetrics(1234, 4.5, 200, 3, Instant.FromUtc(2026, 6, 1, 9, 0)),
                    [
                        new JobSignal(
                            "Deploy",
                            "deploy-production",
                            SignalState.Running,
                            "https://github.com/FixPortal/public-repo/actions/runs/101/job/1",
                            Instant.FromUtc(2026, 6, 1, 12, 15)
                        ),
                    ],
                    [
                        new JobSignal(
                            "Package",
                            "publish-nuget",
                            SignalState.Failure,
                            "https://github.com/FixPortal/public-repo/actions/runs/100/job/2",
                            Instant.FromUtc(2026, 6, 1, 11, 15)
                        ),
                    ],
                    new MergedPullRequest(
                        41,
                        "Ship package",
                        "bob",
                        "public-repo",
                        "https://github.com/FixPortal/public-repo/pull/41",
                        Instant.FromUtc(2026, 6, 1, 8, 0)
                    )
                ),
            ],
            [new SummaryCount("success", 1)],
            new MergedPullRequest(
                41,
                "Ship package",
                "bob",
                "public-repo",
                "https://github.com/FixPortal/public-repo/pull/41",
                Instant.FromUtc(2026, 6, 1, 8, 0)
            ),
            [
                new CiTrendBucket(Instant.FromUtc(2026, 6, 1, 10, 0), CiTrendState.Failing),
                new CiTrendBucket(Instant.FromUtc(2026, 6, 1, 11, 0), CiTrendState.Passing) { IsBackfilled = true },
            ],
            [
                new CiTrendBucket(Instant.FromUtc(2026, 6, 1, 10, 0), CiTrendState.Failing),
                new CiTrendBucket(Instant.FromUtc(2026, 6, 1, 11, 0), CiTrendState.Passing) { IsBackfilled = true },
            ]
        );
        var client = CreateClient(snapshot);

        var response = await client.GetAsync("/api/dashboard/snapshot", TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var expected = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Contracts", "dashboard-snapshot.v1.json"),
                TestContext.Current.CancellationToken
            )
        );
        _ = JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }
}
