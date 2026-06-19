using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubOrgClientHelpersTests
{
    private static readonly DashboardOptions DefaultOptions = new()
    {
        SnapshotPath = "x", RefreshSeconds = 60, ExcludeArchived = true,
        IncludeReusable = false, IncludeCodeQl = true
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
        var opts = new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 60, IncludeCodeQl = false };
        _ = GitHubOrgClient.IncludeWorkflow("CodeQL", "dynamic/github-code-scanning/codeql", opts).Should().BeFalse();
    }

    [Fact]
    public void IncludeWorkflow_keeps_reusable_when_toggled_on()
    {
        var opts = new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 60, IncludeReusable = true };
        _ = GitHubOrgClient.IncludeWorkflow("_deploy (reusable)", ".github/workflows/_deploy.yml", opts).Should().BeTrue();
    }
}
