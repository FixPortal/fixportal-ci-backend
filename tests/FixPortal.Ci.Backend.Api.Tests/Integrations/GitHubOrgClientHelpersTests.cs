using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubOrgClientHelpersTests
{
    private static readonly DashboardOptions DefaultOptions = new()
    {
        SnapshotPath = "x",
        RefreshSeconds = 60,
        ExcludeArchived = true,
        IncludeReusable = false,
        IncludeCodeQl = true,
    };

    [Fact]
    public void ToSignalState_null_run_is_unknown() =>
        GitHubOrgClient.ToSignalState(null).Should().Be(SignalState.Unknown);

    [Theory]
    [InlineData("success", SignalState.Success)]
    [InlineData("failure", SignalState.Failure)]
    [InlineData("timed_out", SignalState.Failure)]
    [InlineData("startup_failure", SignalState.Failure)]
    [InlineData("cancelled", SignalState.Unknown)]
    [InlineData("skipped", SignalState.Unknown)]
    [InlineData("neutral", SignalState.Unknown)]
    public void ToSignalState_maps_completed_conclusions(string conclusion, SignalState expected)
    {
        var run = new WorkflowRun("completed", conclusion, "u", "t", 1, "main", "push", Instant.MinValue);
        _ = GitHubOrgClient.ToSignalState(run).Should().Be(expected);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("queued")]
    [InlineData("waiting")]
    public void ToSignalState_running_when_no_conclusion_yet(string status)
    {
        var run = new WorkflowRun(status, null, "u", "t", 1, "main", "push", Instant.MinValue);
        _ = GitHubOrgClient.ToSignalState(run).Should().Be(SignalState.Running);
    }

    [Theory]
    [InlineData("CI", ".github/workflows/ci.yml", true)]
    [InlineData("_deploy (reusable)", ".github/workflows/_deploy.yml", false)]
    [InlineData("Dependabot Updates", "dynamic/dependabot/dependabot-updates", false)]
    [InlineData("CodeQL", "dynamic/github-code-scanning/codeql", true)]
    public void IncludeWorkflow_applies_scope(string name, string path, bool expected) =>
        GitHubOrgClient.IncludeWorkflow(name, path, DefaultOptions).Should().Be(expected);

    [Fact]
    public void IncludeWorkflow_codeql_excluded_when_toggled_off()
    {
        var opts = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 60,
            IncludeCodeQl = false,
        };
        _ = GitHubOrgClient.IncludeWorkflow("CodeQL", "dynamic/github-code-scanning/codeql", opts).Should().BeFalse();
    }

    [Fact]
    public void IncludeWorkflow_keeps_reusable_when_toggled_on()
    {
        var opts = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 60,
            IncludeReusable = true,
        };
        _ = GitHubOrgClient
            .IncludeWorkflow("_deploy (reusable)", ".github/workflows/_deploy.yml", opts)
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("", "", "", "", "repo", null, true)]
    [InlineData("api-*", "", "", "", "API-Service", null, true)]
    [InlineData("api-*", "", "", "", "worker", null, false)]
    [InlineData("", "LEGACY-*", "", "", "legacy-api", null, false)]
    [InlineData("api-*", "api-secret", "", "", "api-secret", null, false)]
    [InlineData("", "", "dotnet", "", "repo", "DOTNET,backend", true)]
    [InlineData("", "", "dotnet", "", "repo", null, false)]
    [InlineData("api-*", "", "backend", "", "api-service", "frontend", false)]
    [InlineData("", "", "backend", "internal", "repo", "backend,internal", false)]
    public void IncludeRepository_applies_name_and_topic_filters(
        string includeRepositories,
        string excludeRepositories,
        string includeTopics,
        string excludeTopics,
        string name,
        string? topics,
        bool expected
    )
    {
        var options = new DashboardOptions
        {
            SnapshotPath = "x",
            RefreshSeconds = 60,
            IncludeRepositories = Patterns(includeRepositories),
            ExcludeRepositories = Patterns(excludeRepositories),
            IncludeTopics = Patterns(includeTopics),
            ExcludeTopics = Patterns(excludeTopics),
        };
        var repo = new GitHubRepoDto("repo", "https://example.test/repo", false, false, "main", Patterns(topics));

        _ = GitHubOrgClient.IncludeRepository(repo with { Name = name }, options).Should().Be(expected);
    }

    [Fact]
    public async Task ListRepositoriesAsync_applies_filters_and_logs_returned_and_surviving_counts()
    {
        using var http = new HttpClient(
            new StaticJsonHandler(
                """[{"name":"api-public","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":["backend"]},{"name":"api-internal","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":["backend","internal"]},{"name":"worker","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":["backend"]},{"name":"api-frontend","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":["frontend"]},{"name":"api-archived","html_url":"u","private":false,"archived":true,"default_branch":"main","topics":["backend"]}]"""
            )
        );
        http.BaseAddress = new Uri("https://api.github.com/");
        var logger = new CapturingLogger<GitHubOrgClient>();
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "acme", Token = "t" }),
            Options.Create(
                new DashboardOptions
                {
                    SnapshotPath = "x",
                    RefreshSeconds = 60,
                    IncludeRepositories = ["api-*"],
                    IncludeTopics = ["backend"],
                    ExcludeTopics = ["internal"],
                }
            ),
            new GitHubETagStore(),
            logger: logger
        );

        var repositories = await client.ListRepositoriesAsync(CancellationToken.None);

        _ = repositories.Should().ContainSingle().Which.Name.Should().Be("api-public");
        _ = repositories[0].Topics.Should().Equal("backend");
        _ = logger
            .Entries.Should()
            .ContainSingle(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Information
                && e.Message.Contains("5 repositories", StringComparison.Ordinal)
                && e.Message.Contains("1 remain", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task ListRepositoriesAsync_without_filters_keeps_all_non_archived_repositories()
    {
        using var http = new HttpClient(
            new StaticJsonHandler(
                """[{"name":"one","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":[]},{"name":"two","html_url":"u","private":false,"archived":false,"default_branch":"main","topics":["backend"]},{"name":"old","html_url":"u","private":false,"archived":true,"default_branch":"main","topics":[]}]"""
            )
        );
        http.BaseAddress = new Uri("https://api.github.com/");
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "acme", Token = "t" }),
            Options.Create(DefaultOptions),
            new GitHubETagStore()
        );

        var repositories = await client.ListRepositoriesAsync(CancellationToken.None);

        _ = repositories.Select(r => r.Name).Should().Equal("one", "two");
    }

    [Fact]
    public async Task GetRecentRunsAsync_maps_exact_GitHub_run_identity()
    {
        using var http = new HttpClient(
            new StaticJsonHandler(
                """{"workflow_runs":[{"id":9876543210,"run_attempt":3,"head_sha":"0123456789abcdef0123456789abcdef01234567","status":"completed","conclusion":"success","html_url":"https://github.com/acme/repo/actions/runs/9876543210","display_title":"Build","run_number":7,"head_branch":"main","event":"push","updated_at":"2026-01-01T00:00:00Z"}]}"""
            )
        );
        http.BaseAddress = new Uri("https://api.github.com/");
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "acme", Token = "t" }),
            Options.Create(DefaultOptions),
            new GitHubETagStore()
        );

        var run = (
            await client.GetRecentRunsAsync(
                "repo",
                new GitHubWorkflowDto(3, "Build", ".github/workflows/ci.yml", "active"),
                CancellationToken.None
            )
        )
            .Should()
            .ContainSingle()
            .Subject;

        _ = run.ProviderRunId.Should().Be(9876543210);
        _ = run.RunAttempt.Should().Be(3);
        _ = run.HeadSha.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
    }

    private static IReadOnlyList<string> Patterns(string? value) => string.IsNullOrEmpty(value) ? [] : value.Split(',');
}
