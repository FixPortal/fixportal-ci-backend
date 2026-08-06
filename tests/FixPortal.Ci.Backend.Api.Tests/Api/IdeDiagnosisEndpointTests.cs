using System.IO.Compression;
using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Ide;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Api;

public sealed class IdeDiagnosisEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>,
        IDisposable
{
    private const string IdeKey = "ide-integration-key-012345678901234";
    private const string Route = "/api/ide/v1/repositories/alpha/runs/42/diagnosis";
    private readonly List<HttpClient> _clients = [];
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly List<ProviderHandler> _handlers = [];

    public void Dispose()
    {
        _clients.ForEach(client => client.Dispose());
        _factories.ForEach(configuredFactory => configuredFactory.Dispose());
        _handlers.ForEach(handler => handler.Dispose());
    }

    [Fact]
    public async Task Authentication_precedes_reader_resolution_and_provider_use()
    {
        var resolutions = 0;
        var state = SeedState(Snapshot());
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            Configure(builder, state);
            _ = builder.ConfigureServices(services =>
            {
                _ = services.RemoveAll<RunDiagnosisReader>();
                _ = services.AddSingleton<RunDiagnosisReader>(_ =>
                {
                    resolutions++;
                    throw new InvalidOperationException("reader was resolved");
                });
            });
        });
        _factories.Add(configuredFactory);
        var client = configuredFactory.CreateClient();
        _clients.Add(client);

        using var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resolutions.Should().Be(0);
    }

    [Theory]
    [InlineData("missing", 42)]
    [InlineData("alpha", 999)]
    public async Task Unknown_repository_or_run_is_unavailable_without_a_provider_call(string repository, long runId)
    {
        var handler = new ProviderHandler(_ => throw new InvalidOperationException("provider was used"));
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(
            Request($"/api/ide/v1/repositories/{repository}/runs/{runId}/diagnosis"),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be("{\"error\":\"Diagnosis is unavailable.\"}");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Diagnosis_responses_are_never_cached_and_vary_by_IDE_key()
    {
        var handler = SuccessfulProvider("hello");
        var client = CreateClient(Snapshot(), handler);

        using var ok = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);
        using var unavailable = await client.SendAsync(
            Request("/api/ide/v1/repositories/missing/runs/42/diagnosis"),
            TestContext.Current.CancellationToken
        );
        using var unauthorized = await client.GetAsync(Route, TestContext.Current.CancellationToken);

        foreach (var response in new[] { ok, unavailable, unauthorized })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            response.Headers.Vary.Should().ContainSingle().Which.Should().Be("X-CI-IDE-Key");
        }
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("completed", "success")]
    public async Task Running_or_successful_runs_are_unavailable(string status, string? conclusion)
    {
        var handler = new ProviderHandler(_ => throw new InvalidOperationException("provider was used"));
        var client = CreateClient(Snapshot(FailedRun() with { Status = status, Conclusion = conclusion }), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("runId")]
    [InlineData("attempt")]
    [InlineData("sha")]
    [InlineData("workflow")]
    public async Task Snapshot_identity_must_repeat_the_route_and_containing_workflow(string mismatch)
    {
        var run = mismatch switch
        {
            "repository" => FailedRun() with { Repository = "FixPortal/Other" },
            "runId" => FailedRun() with { ProviderRunId = 43 },
            "attempt" => FailedRun() with { RunAttempt = 0 },
            "sha" => FailedRun() with { HeadSha = "ABC" },
            "workflow" => FailedRun() with { WorkflowFile = ".github/workflows/other.yml" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var handler = new ProviderHandler(_ => throw new InvalidOperationException("provider was used"));
        var client = CreateClient(Snapshot(run), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Diagnosis_matches_the_v1_golden_fixture_and_preserves_snapshot_spelling()
    {
        var handler = SuccessfulProvider("hello");
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);
        var actual = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var expected = await File.ReadAllBytesAsync(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/ci-ide-diagnosis-v1.json")),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        actual.Should().Equal(expected);
    }

    [Fact]
    public async Task Diagnosis_resolves_a_run_from_a_production_shaped_filename_snapshot()
    {
        var handler = SuccessfulProvider("hello");
        var client = CreateClient(Snapshot(workflowFile: "build.yml"), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);
        var actual = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var expected = await File.ReadAllBytesAsync(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/ci-ide-diagnosis-v1.json")),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        actual.Should().Equal(expected);
    }

    [Fact]
    public async Task Provider_timeout_returns_a_fixed_gateway_timeout()
    {
        var handler = new ProviderHandler(_ => throw new TaskCanceledException("secret provider detail"));
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be("{\"error\":\"Diagnosis provider timed out.\"}");
    }

    [Fact]
    public async Task Exactly_one_https_redirect_is_followed_without_GitHub_headers()
    {
        var handler = SuccessfulProvider("hello");
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().HaveCount(2);
        var first = handler.Requests[0];
        first.Uri.Should().Be(new Uri("https://api.github.com/repos/FixPortal/Alpha/actions/runs/42/logs"));
        first.Authorization.Should().Be("Bearer test-token");
        first.Accept.Should().Contain("application/vnd.github+json");
        first.IfNoneMatch.Should().BeEmpty();
        var storage = handler.Requests[1];
        storage.Uri.Should().Be(new Uri("https://storage.example.test/run.zip"));
        storage.Authorization.Should().BeNull();
        storage.Accept.Should().BeEmpty();
        storage.IfNoneMatch.Should().BeEmpty();
        storage.GitHubApiVersion.Should().BeNull();
    }

    [Theory]
    [InlineData("http://storage.example.test/run.zip")]
    [InlineData("/relative/run.zip")]
    public async Task Non_https_or_non_absolute_redirect_is_rejected(string location)
    {
        var handler = new ProviderHandler(_ => Redirect(location));
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_redirect_is_rejected()
    {
        var handler = new ProviderHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://storage.example.test/run.zip")
                : Redirect("https://storage.example.test/again.zip")
        );
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Expired_provider_logs_are_unavailable_without_mutating_auth_health(HttpStatusCode status)
    {
        var state = SeedState(Snapshot());
        state.SetAuthError("existing auth state");
        var handler = new ProviderHandler(_ => new HttpResponseMessage(status));
        var client = CreateClient(state, handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        state.LastAuthError.Should().Be("existing auth state");
    }

    [Fact]
    public async Task Rejected_provider_content_returns_a_fixed_identity_free_bounded_error()
    {
        const string secret = "private-repo-secret-token-123";
        var run = FailedRun() with { Repository = $"FixPortal/{secret}" };
        var handler = new ProviderHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://storage.example.test/run.zip")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1]),
                }
        );
        var client = CreateClient(Snapshot(run, secret), handler);

        using var response = await client.SendAsync(
            Request($"/api/ide/v1/repositories/{secret}/runs/42/diagnosis"),
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Be("{\"error\":\"Diagnosis provider response was rejected.\"}");
        body.Should().NotContain(secret);
        body.Length.Should().BeLessThan(128);
    }

    [Theory]
    [InlineData("oversized central offset")]
    [InlineData("local encryption flag")]
    public async Task Malformed_zip_headers_return_the_fixed_rejection(string corruption)
    {
        var archive = Zip("hello");
        CorruptHeader(archive, corruption);
        var handler = new ProviderHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://storage.example.test/run.zip")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
        );
        var client = CreateClient(Snapshot(), handler);

        using var response = await client.SendAsync(Request(Route), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Be("{\"error\":\"Diagnosis provider response was rejected.\"}");
    }

    private HttpClient CreateClient(DashboardSnapshot snapshot, ProviderHandler handler) =>
        CreateClient(SeedState(snapshot), handler);

    private HttpClient CreateClient(DashboardSnapshotState state, ProviderHandler handler)
    {
        _handlers.Add(handler);
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            Configure(builder, state);
            _ = builder.ConfigureServices(services =>
            {
                _ = services.RemoveAll<GitHubOrgClient>();
                _ = services.RemoveAll<RunDiagnosisReader>();
                var githubHttp = new HttpClient(handler, disposeHandler: false)
                {
                    BaseAddress = new Uri("https://api.github.com/"),
                };
                _clients.Add(githubHttp);
                var github = new GitHubOrgClient(
                    githubHttp,
                    Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "test-token" }),
                    Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
                    new GitHubETagStore(),
                    state
                );
                _ = services.AddSingleton(github);
                var readerHttp = new HttpClient(handler, disposeHandler: false);
                _clients.Add(readerHttp);
                _ = services.AddSingleton(new RunDiagnosisReader(readerHttp, github));
            });
        });
        _factories.Add(configuredFactory);
        var client = configuredFactory.CreateClient();
        _clients.Add(client);
        return client;
    }

    private static void Configure(IWebHostBuilder builder, DashboardSnapshotState state)
    {
        _ = builder.UseSetting("GitHub:Owner", "FixPortal");
        _ = builder.UseSetting("GitHub:Token", "test-token");
        _ = builder.UseSetting("IdeIntegration:ApiKey", IdeKey);
        _ = builder.ConfigureServices(services =>
        {
            _ = services.RemoveAll<IHostedService>();
            _ = services.RemoveAll<DashboardSnapshotState>();
            _ = services.AddSingleton(state);
        });
    }

    private static HttpRequestMessage Request(string route) =>
        new(HttpMethod.Get, route) { Headers = { { "X-CI-IDE-Key", IdeKey } } };

    private static DashboardSnapshotState SeedState(DashboardSnapshot snapshot)
    {
        var state = new DashboardSnapshotState();
        state.Update(snapshot, DashboardSnapshotState.ComputePublicSnapshot(snapshot, snapshot.PublicCiTrend));
        return state;
    }

    private static DashboardSnapshot Snapshot(
        WorkflowRun? run = null,
        string repository = "Alpha",
        string workflowFile = ".github/workflows/build.yml"
    ) =>
        new(
            Instant.FromUtc(2026, 8, 5, 10, 0),
            "FixPortal",
            [
                new RepositorySnapshot(
                    repository,
                    $"https://github.com/FixPortal/{repository}",
                    true,
                    [
                        new WorkflowSnapshot(
                            "Build",
                            workflowFile,
                            SignalState.Failure,
                            null,
                            [run ?? FailedRun(repository, workflowFile)]
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

    private static WorkflowRun FailedRun(
        string repository = "Alpha",
        string workflowFile = ".github/workflows/build.yml"
    ) =>
        new(
            "completed",
            "failure",
            "https://github.com/FixPortal/Alpha/actions/runs/42",
            "build",
            7,
            "main",
            "push",
            Instant.FromUtc(2026, 8, 5, 9, 0),
            $"FixPortal/{repository}",
            workflowFile,
            42,
            3,
            "cccccccccccccccccccccccccccccccccccccccc"
        );

    private static ProviderHandler SuccessfulProvider(string text) =>
        new(request =>
            request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://storage.example.test/run.zip")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(text)) }
        );

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static byte[] Zip(string text)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("job.txt").Open())
        {
            entry.Write(Encoding.UTF8.GetBytes(text));
        }
        return stream.ToArray();
    }

    private static void CorruptHeader(byte[] archive, string corruption)
    {
        var signature = corruption == "oversized central offset" ? 0x06054b50u : 0x04034b50u;
        var offset = FindSignature(archive, signature);
        if (corruption == "oversized central offset")
        {
            BitConverter.GetBytes(uint.MaxValue).CopyTo(archive, offset + 16);
            return;
        }

        archive[offset + 6] |= 1;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        var expected = BitConverter.GetBytes(signature);
        for (var index = 0; index <= bytes.Length - expected.Length; index++)
        {
            if (bytes.AsSpan(index, expected.Length).SequenceEqual(expected))
            {
                return index;
            }
        }
        throw new InvalidOperationException("ZIP signature not found.");
    }

    private sealed class ProviderHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(
                new RecordedRequest(
                    request.RequestUri!,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.Accept.Select(value => value.MediaType!).ToArray(),
                    request.Headers.IfNoneMatch.Select(value => value.ToString()).ToArray(),
                    request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? versions.Single() : null
                )
            );
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RecordedRequest(
        Uri Uri,
        string? Authorization,
        IReadOnlyList<string> Accept,
        IReadOnlyList<string> IfNoneMatch,
        string? GitHubApiVersion
    );
}
