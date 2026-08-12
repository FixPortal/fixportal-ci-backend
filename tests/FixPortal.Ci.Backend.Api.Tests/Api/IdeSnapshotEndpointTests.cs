using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public sealed class IdeSnapshotEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>,
        IDisposable
{
    private const string IdeKey = "ide-integration-key-012345678901234";
    private readonly List<HttpClient> _clients = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factories.ForEach(configuredFactory => configuredFactory.Dispose());
    }

    private HttpClient CreateClient(
        DashboardSnapshot? seed,
        string? ideKey = IdeKey,
        string? adminKey = null,
        int? runHistoryPageSize = null
    ) => CreateClientWithState(seed, ideKey, adminKey, runHistoryPageSize).Client;

    private (HttpClient Client, DashboardSnapshotState State) CreateClientWithState(
        DashboardSnapshot? seed,
        string? ideKey = IdeKey,
        string? adminKey = null,
        int? runHistoryPageSize = null
    )
    {
        var state = new DashboardSnapshotState();
        if (seed is not null)
        {
            state.Update(seed, DashboardSnapshotState.ComputePublicSnapshot(seed, seed.PublicCiTrend));
        }

        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Token", "test-token");
            if (ideKey is not null)
            {
                _ = builder.UseSetting("IdeIntegration:ApiKey", ideKey);
            }
            if (adminKey is not null)
            {
                _ = builder.UseSetting("Admin:AdminKey", adminKey);
            }
            if (runHistoryPageSize is not null)
            {
                _ = builder.UseSetting("Dashboard:RunHistoryPageSize", runHistoryPageSize.Value.ToString());
            }
            _ = builder.ConfigureServices(services =>
            {
                _ = services.RemoveAll<IHostedService>();
                _ = services.RemoveAll<DashboardSnapshotState>();
                _ = services.AddSingleton(state);
            });
        });
        _factories.Add(configuredFactory);
        var client = configuredFactory.CreateClient();
        _clients.Add(client);
        return (client, state);
    }

    private static HttpRequestMessage Request(string key = IdeKey) =>
        new(HttpMethod.Get, "/api/ide/v1/snapshot") { Headers = { { "X-CI-IDE-Key", key } } };

    private static DashboardSnapshot Snapshot(
        Instant? refreshedAt = null,
        bool includeInvalidRun = false,
        string buildWorkflowFile = ".github/workflows/build.yml"
    ) =>
        new(
            refreshedAt ?? Instant.FromUtc(2026, 8, 5, 10, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    "zeta",
                    "https://github.com/FixPortal/zeta",
                    true,
                    [
                        new WorkflowSnapshot(
                            "Release",
                            ".github/workflows/release.yml",
                            SignalState.Success,
                            null,
                            [
                                new WorkflowRun(
                                    "completed",
                                    "success",
                                    "https://github.com/FixPortal/zeta/actions/runs/12",
                                    "release",
                                    4,
                                    "main",
                                    "push",
                                    Instant.FromUtc(2026, 8, 5, 9, 3),
                                    "FixPortal/zeta",
                                    ".github/workflows/release.yml",
                                    12,
                                    2,
                                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                                ),
                            ]
                        ),
                    ],
                    [],
                    null,
                    [],
                    []
                ),
                new RepositorySnapshot(
                    "Alpha",
                    "https://github.com/FixPortal/Alpha",
                    false,
                    [
                        new WorkflowSnapshot(
                            "Validate",
                            ".github/workflows/validate.yml",
                            SignalState.Success,
                            null,
                            [
                                new WorkflowRun(
                                    "completed",
                                    "success",
                                    "https://github.com/FixPortal/Alpha/actions/runs/3",
                                    "validate",
                                    3,
                                    "main",
                                    "pull_request",
                                    Instant.FromUtc(2026, 8, 5, 9, 2),
                                    "FixPortal/Alpha",
                                    ".github/workflows/validate.yml",
                                    3,
                                    1,
                                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                                ),
                            ]
                        ),
                        new WorkflowSnapshot(
                            "Build",
                            buildWorkflowFile,
                            SignalState.Failure,
                            null,
                            [
                                new WorkflowRun(
                                    "completed",
                                    "failure",
                                    "https://github.com/FixPortal/Alpha/actions/runs/2",
                                    "build",
                                    2,
                                    "main",
                                    "push",
                                    Instant.FromUtc(2026, 8, 5, 9, 1),
                                    "FixPortal/Alpha",
                                    buildWorkflowFile,
                                    2,
                                    1,
                                    includeInvalidRun
                                        ? "UPPERCASE-SHA-IS-NOT-VALID"
                                        : "cccccccccccccccccccccccccccccccccccccccc"
                                ),
                            ]
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

    private static DashboardSnapshot SnapshotWithBuildRuns(params WorkflowRun[] runs)
    {
        var snapshot = Snapshot();
        return snapshot with
        {
            Repositories = snapshot
                .Repositories.Select(repository =>
                    repository.Name == "Alpha"
                        ? repository with
                        {
                            Workflows = repository
                                .Workflows.Select(workflow =>
                                    workflow.File == ".github/workflows/build.yml"
                                        ? workflow with
                                        {
                                            RecentRuns = runs,
                                        }
                                        : workflow
                                )
                                .ToList(),
                        }
                        : repository
                )
                .ToList(),
        };
    }

    private static DashboardSnapshot SnapshotWithBuildAlias(bool aliasFirst)
    {
        var snapshot = Snapshot();
        return snapshot with
        {
            Repositories = snapshot
                .Repositories.Select(repository =>
                {
                    if (repository.Name != "Alpha")
                    {
                        return repository;
                    }

                    var workflows = repository.Workflows.ToList();
                    var build = workflows.Single(workflow => workflow.File == ".github/workflows/build.yml");
                    var alias = build with
                    {
                        File = "build.yml",
                        RecentRuns = build.RecentRuns!.Select(run => run with { WorkflowFile = "build.yml" }).ToList(),
                    };
                    if (aliasFirst)
                    {
                        workflows.Insert(0, alias);
                    }
                    else
                    {
                        workflows.Add(alias);
                    }
                    return repository with { Workflows = workflows };
                })
                .ToList(),
        };
    }

    private static WorkflowRun Run(long id, Instant updatedAt) =>
        new(
            "completed",
            "success",
            $"https://github.com/FixPortal/Alpha/actions/runs/{id}",
            "build",
            (int)id,
            "main",
            "push",
            updatedAt,
            "FixPortal/Alpha",
            ".github/workflows/build.yml",
            id,
            1,
            id.ToString("x40")
        );

    [Fact]
    public async Task Snapshot_rejects_missing_or_wrong_key()
    {
        var client = CreateClient(Snapshot());

        using var missing = await client.GetAsync("/api/ide/v1/snapshot", TestContext.Current.CancellationToken);
        using var wrong = await client.SendAsync(Request("wrong-key"), TestContext.Current.CancellationToken);

        _ = missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Snapshot_responses_are_never_cached_and_vary_by_IDE_key()
    {
        var client = CreateClient(Snapshot());

        using var ok = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var unauthorized = await client.GetAsync("/api/ide/v1/snapshot", TestContext.Current.CancellationToken);
        using var conditional = Request();
        conditional.Headers.IfNoneMatch.Add(ok.Headers.ETag!);
        using var notModified = await client.SendAsync(conditional, TestContext.Current.CancellationToken);
        var emptyClient = CreateClient(seed: null);
        using var noContent = await emptyClient.SendAsync(Request(), TestContext.Current.CancellationToken);

        foreach (var response in new[] { ok, unauthorized, notModified, noContent })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            response.Headers.Vary.Should().ContainSingle().Which.Should().Be("X-CI-IDE-Key");
        }
    }

    [Fact]
    public void Reusing_the_admin_key_for_the_IDE_key_is_rejected_at_startup()
    {
        var act = () => CreateClient(Snapshot(), IdeKey, IdeKey);

        _ = act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage(
                "*IdeIntegration:ApiKey, when set, must be unpadded, resolved, at least 32 characters, and distinct from Admin:AdminKey.*"
            );
    }

    [Theory]
    [InlineData(" ide-integration-key-012345678901234")]
    [InlineData("{{this-is-an-unresolved-placeholder-longer-than-32-characters}}")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://example.vault.azure.net/secrets/key)")]
    public void Padded_or_placeholder_IDE_keys_are_rejected_at_startup(string invalidKey)
    {
        var act = () => CreateClient(Snapshot(), invalidKey);

        _ = act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage(
                "*IdeIntegration:ApiKey, when set, must be unpadded, resolved, at least 32 characters, and distinct from Admin:AdminKey.*"
            );
    }

    [Fact]
    public async Task Snapshot_includes_private_repositories_and_sorts_repositories_and_workflows_ordinally()
    {
        var client = CreateClient(Snapshot());

        using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repositories = json.RootElement.GetProperty("repositories");
        _ = repositories[0].GetProperty("name").GetString().Should().Be("Alpha");
        _ = repositories[0].GetProperty("private").GetBoolean().Should().BeFalse();
        _ = repositories[1].GetProperty("name").GetString().Should().Be("zeta");
        _ = repositories[1].GetProperty("private").GetBoolean().Should().BeTrue();
        var workflows = repositories[0].GetProperty("workflows");
        _ = workflows[0].GetProperty("file").GetString().Should().Be(".github/workflows/build.yml");
        _ = workflows[1].GetProperty("file").GetString().Should().Be(".github/workflows/validate.yml");
    }

    [Fact]
    public async Task Stored_workflow_filenames_project_to_the_canonical_IDE_contract_without_changing_content_identity()
    {
        var canonicalClient = CreateClient(Snapshot());
        var filenameClient = CreateClient(Snapshot(buildWorkflowFile: "build.yml"));

        using var canonical = await canonicalClient.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var filename = await filenameClient.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(
            await filename.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var build = json
            .RootElement.GetProperty("repositories")[0]
            .GetProperty("workflows")
            .EnumerateArray()
            .Single(workflow => workflow.GetProperty("file").GetString() == ".github/workflows/build.yml");

        filename.StatusCode.Should().Be(HttpStatusCode.OK);
        build.GetProperty("recentRuns").EnumerateArray().Single().GetProperty("runId").GetInt64().Should().Be(2);
        filename.Headers.ETag.Should().Be(canonical.Headers.ETag);
    }

    [Fact]
    public async Task Canonical_aliases_are_omitted_as_ambiguous_with_deterministic_content_identity()
    {
        var canonicalFirstClient = CreateClient(SnapshotWithBuildAlias(aliasFirst: false));
        var filenameFirstClient = CreateClient(SnapshotWithBuildAlias(aliasFirst: true));

        using var canonicalFirst = await canonicalFirstClient.SendAsync(
            Request(),
            TestContext.Current.CancellationToken
        );
        using var filenameFirst = await filenameFirstClient.SendAsync(Request(), TestContext.Current.CancellationToken);
        var canonicalFirstBytes = await canonicalFirst.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken
        );
        var filenameFirstBytes = await filenameFirst.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken
        );
        using var json = JsonDocument.Parse(canonicalFirstBytes);
        var workflows = json.RootElement.GetProperty("repositories")[0].GetProperty("workflows");

        workflows
            .EnumerateArray()
            .Select(workflow => workflow.GetProperty("file").GetString())
            .Should()
            .Equal(".github/workflows/validate.yml");
        canonicalFirstBytes.Should().Equal(filenameFirstBytes);
        canonicalFirst.Headers.ETag.Should().Be(filenameFirst.Headers.ETag);
    }

    [Theory]
    [InlineData("../build.yml")]
    [InlineData("nested/build.yml")]
    [InlineData("nested\\build.yml")]
    [InlineData(".")]
    [InlineData(" ")]
    [InlineData("build\0.yml")]
    [InlineData("build\u001f.yml")]
    public async Task Invalid_basename_and_canonical_workflow_identities_are_omitted(string workflowFile)
    {
        foreach (var stored in new[] { workflowFile, $".github/workflows/{workflowFile}" })
        {
            var client = CreateClient(Snapshot(buildWorkflowFile: stored));

            using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            var workflows = json.RootElement.GetProperty("repositories")[0].GetProperty("workflows");

            workflows
                .EnumerateArray()
                .Select(workflow => workflow.GetProperty("file").GetString())
                .Should()
                .Equal(".github/workflows/validate.yml");
        }
    }

    [Theory]
    [InlineData("Build Name.yml")]
    [InlineData("\u00fcber.yml")]
    [InlineData("CI;release.yml")]
    public async Task Valid_direct_child_basenames_are_preserved(string workflowFile)
    {
        foreach (var stored in new[] { workflowFile, $".github/workflows/{workflowFile}" })
        {
            var client = CreateClient(Snapshot(buildWorkflowFile: stored));

            using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            var projected = json
                .RootElement.GetProperty("repositories")[0]
                .GetProperty("workflows")
                .EnumerateArray()
                .Single(workflow => workflow.GetProperty("file").GetString() == $".github/workflows/{workflowFile}");

            projected.GetProperty("recentRuns").GetArrayLength().Should().Be(1);
        }
    }

    [Fact]
    public async Task Workflow_file_over_the_IDE_contract_UTF8_limit_is_omitted()
    {
        var workflowFile = new string('\u00e9', 61) + ".yml";
        var client = CreateClient(Snapshot(buildWorkflowFile: workflowFile));

        using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var workflows = json.RootElement.GetProperty("repositories")[0].GetProperty("workflows");

        workflows
            .EnumerateArray()
            .Select(workflow => workflow.GetProperty("file").GetString())
            .Should()
            .Equal(".github/workflows/validate.yml");
    }

    [Fact]
    public async Task An_ineligible_run_leaves_its_canonical_workflow_with_an_empty_history()
    {
        var client = CreateClient(Snapshot(includeInvalidRun: true));

        using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);

        var workflows = json.RootElement.GetProperty("repositories")[0].GetProperty("workflows");
        _ = workflows[0].GetProperty("file").GetString().Should().Be(".github/workflows/build.yml");
        _ = workflows[0].GetProperty("recentRuns").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_caps_restored_history_at_the_current_configured_page_size()
    {
        var client = CreateClient(
            SnapshotWithBuildRuns(
                Run(3, Instant.FromUtc(2026, 8, 5, 9, 3)),
                Run(2, Instant.FromUtc(2026, 8, 5, 9, 2)),
                Run(1, Instant.FromUtc(2026, 8, 5, 9, 1))
            ),
            runHistoryPageSize: 2
        );

        using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        var runs = json
            .RootElement.GetProperty("repositories")[0]
            .GetProperty("workflows")[0]
            .GetProperty("recentRuns");

        _ = runs.GetArrayLength().Should().Be(2);
        _ = runs.EnumerateArray().Select(run => run.GetProperty("runId").GetInt64()).Should().Equal(3, 2);
    }

    [Fact]
    public async Task Equivalent_tied_run_histories_have_identical_snapshot_bytes_and_etags()
    {
        var updatedAt = Instant.FromUtc(2026, 8, 5, 9, 3);
        var firstClient = CreateClient(SnapshotWithBuildRuns(Run(2, updatedAt), Run(1, updatedAt)));
        var secondClient = CreateClient(SnapshotWithBuildRuns(Run(1, updatedAt), Run(2, updatedAt)));

        using var first = await firstClient.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var second = await secondClient.SendAsync(Request(), TestContext.Current.CancellationToken);
        var firstBytes = await first.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var secondBytes = await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        _ = firstBytes.Should().Equal(secondBytes);
        _ = first.Headers.ETag.Should().Be(second.Headers.ETag);
    }

    [Fact]
    public async Task Snapshot_matches_the_v1_golden_fixture_with_schema_and_weak_etag()
    {
        var client = CreateClient(Snapshot());

        using var response = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        var actual = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var expected = await File.ReadAllBytesAsync(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/ci-ide-snapshot-v1.json")),
            TestContext.Current.CancellationToken
        );
        using var json = JsonDocument.Parse(actual);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        _ = response
            .Headers.ETag!.ToString()
            .Should()
            .Be($"W/\"{json.RootElement.GetProperty("snapshotId").GetString()}\"");
        _ = actual.Should().Equal(expected);
    }

    [Fact]
    public async Task Snapshot_content_has_a_stable_etag_when_only_observed_at_changes()
    {
        var (client, state) = CreateClientWithState(Snapshot());

        using var first = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        var firstBody = await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var next = Snapshot(Instant.FromUtc(2026, 8, 5, 10, 1));
        state.Update(next, DashboardSnapshotState.ComputePublicSnapshot(next, next.PublicCiTrend));
        using var second = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        var secondBody = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        _ = second.Headers.ETag.Should().Be(first.Headers.ETag);
        _ = secondBody.Should().NotBe(firstBody);
    }

    [Fact]
    public async Task An_authenticated_matching_etag_returns_not_modified()
    {
        var client = CreateClient(Snapshot());
        using var first = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var conditional = Request();
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await client.SendAsync(conditional, TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task A_matching_etag_never_bypasses_authentication()
    {
        var client = CreateClient(Snapshot());
        using var first = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var conditional = Request("wrong-key");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await client.SendAsync(conditional, TestContext.Current.CancellationToken);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_configured_key_returns_204_before_a_snapshot_but_no_key_is_401()
    {
        var configured = CreateClient(seed: null);
        var unconfigured = CreateClient(seed: null, ideKey: null);

        using var ready = await configured.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var denied = await unconfigured.SendAsync(Request(), TestContext.Current.CancellationToken);

        _ = ready.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _ = denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;

[Collection("Environment variables")]
public sealed class IdeIntegrationEnvironmentVariableTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string IdeKey = "ide-integration-key-012345678901234";

    [Fact]
    public void IdeIntegration_environment_variable_path_binds_to_the_options_value()
    {
        var original = Environment.GetEnvironmentVariable("IdeIntegration__ApiKey");
        Environment.SetEnvironmentVariable("IdeIntegration__ApiKey", IdeKey);
        try
        {
            using var localFactory = factory.WithWebHostBuilder(builder =>
            {
                _ = builder.UseSetting("GitHub:Token", "test-token");
                _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });
            var optionsType = typeof(Program).Assembly.GetType("FixPortal.Ci.Backend.Api.Ide.IdeIntegrationOptions");

            _ = optionsType.Should().NotBeNull();
            var closedOptions = typeof(IOptions<>).MakeGenericType(optionsType!);
            var options = localFactory.Services.GetRequiredService(closedOptions);
            var value = options.GetType().GetProperty("Value")!.GetValue(options)!;

            _ = value.GetType().GetProperty("ApiKey")!.GetValue(value).Should().Be(IdeKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IdeIntegration__ApiKey", original);
        }
    }
}
