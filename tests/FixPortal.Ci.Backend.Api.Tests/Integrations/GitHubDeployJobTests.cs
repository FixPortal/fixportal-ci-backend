using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubDeployJobTests
{
    private static readonly string[] DefaultPatterns = ["deploy"];

    private static GitHubJobDto Job(string name, string status, string? conclusion) =>
        new(
            name,
            status,
            conclusion,
            $"https://github.com/job/{name}",
            Instant.FromUnixTimeSeconds(1),
            Instant.FromUnixTimeSeconds(2)
        );

    private static RunWithJobs Run(long id, string status, params GitHubJobDto[] jobs) =>
        new(new GitHubRunSummary(id, $"https://github.com/run/{id}", status, null), jobs);

    private static IReadOnlyList<JobSignal> Select(params RunWithJobs[] runs) =>
        GitHubOrgClient.SelectLaneSignals("CI", "https://github.com/FixPortal/repo", runs, DefaultPatterns).Signals;

    [Theory]
    [InlineData("Deploy (fixportal-prod)", true)]
    [InlineData("Deploy (centerprise-dev)", true)]
    [InlineData("Deploy (Azure Container Apps)", true)]
    [InlineData("deploy", true)]
    [InlineData("Backend (.NET)", false)]
    [InlineData("CodeQL", false)]
    [InlineData("stryker", false)]
    public void IsJobMatch_matches_deploy_named_jobs(string name, bool expected) =>
        GitHubOrgClient.IsJobMatch(name, DefaultPatterns).Should().Be(expected);

    [Theory]
    [InlineData("Publish Docker image", true)]
    [InlineData("publish-demo-host", true)]
    [InlineData("Build and push image", true)]
    [InlineData("release", true)]
    [InlineData("Deploy (prod)", false)]
    [InlineData("Backend (.NET)", false)]
    public void IsJobMatch_matches_package_named_jobs(string name, bool expected)
    {
        string[] patterns = ["publish", "package", "docker", "image", "release", "ghcr"];
        _ = GitHubOrgClient.IsJobMatch(name, patterns).Should().Be(expected);
    }

    [Theory]
    [InlineData("completed", "success", SignalState.Success)]
    [InlineData("completed", "failure", SignalState.Failure)]
    [InlineData("completed", "timed_out", SignalState.Failure)]
    [InlineData("completed", "skipped", SignalState.Unknown)]
    [InlineData("completed", "neutral", SignalState.Unknown)]
    [InlineData("in_progress", null, SignalState.Running)]
    [InlineData("queued", null, SignalState.Running)]
    [InlineData("waiting", null, SignalState.Running)]
    [InlineData("unmapped_status", null, SignalState.Unknown)]
    public void ToSignalState_from_status_conclusion(string? status, string? conclusion, SignalState expected) =>
        GitHubOrgClient.ToSignalState(status, conclusion).Should().Be(expected);

    [Theory]
    [InlineData("Deploy (prod) / Deploy (prod)", "Deploy (prod)")]
    [InlineData("Deploy (prod)", "Deploy (prod)")]
    [InlineData("Deploy (dev) / deploy", "Deploy (dev) / deploy")] // genuinely different segments preserved
    public void CanonicalJobTarget_collapses_repeated_reusable_segments(string name, string expected) =>
        GitHubOrgClient.CanonicalJobTarget(name).Should().Be(expected);

    // The bug: the gated prod deploy is skipped in the newest run while the ungated dev
    // deploy succeeds, so the prior "first run with any signal" logic returned at the
    // newest run carrying only dev and never reached the older run where prod actually ran.
    [Fact]
    public void SelectLaneSignals_surfaces_gated_prod_from_an_older_run_not_just_latest_dev()
    {
        var signals = Select(
            Run(
                170,
                "completed",
                Job("Backend (.NET)", "completed", "success"),
                Job("Deploy (centerprise-dev) / Deploy (centerprise-dev)", "completed", "success"),
                Job("Deploy (fixportal-prod)", "completed", "skipped")
            ),
            Run(
                167,
                "completed",
                Job("Deploy (centerprise-dev) / Deploy (centerprise-dev)", "completed", "success"),
                Job("Deploy (fixportal-prod)", "completed", "skipped")
            ),
            Run(
                164,
                "completed",
                Job("Deploy (centerprise-dev) / Deploy (centerprise-dev)", "completed", "success"),
                Job("Deploy (fixportal-prod) / Deploy (fixportal-prod)", "completed", "success")
            )
        );

        _ = signals.Should().HaveCount(2);
        _ = signals.Should().Contain(s => s.Name.Contains("centerprise-dev") && s.State == SignalState.Success);
        _ = signals.Should().Contain(s => s.Name.Contains("fixportal-prod") && s.State == SignalState.Success);
    }

    [Fact]
    public void SelectLaneSignals_keeps_the_newest_run_for_a_target()
    {
        var signals = Select(
            Run(170, "completed", Job("Deploy (fixportal-prod) / Deploy (fixportal-prod)", "completed", "failure")),
            Run(164, "completed", Job("Deploy (fixportal-prod) / Deploy (fixportal-prod)", "completed", "success"))
        );

        _ = signals.Should().ContainSingle().Which.State.Should().Be(SignalState.Failure);
    }

    [Fact]
    public void SelectLaneSignals_returns_empty_for_a_workflow_with_no_deploy_jobs()
    {
        var signals = Select(Run(1, "completed", Job("Backend (.NET)", "completed", "success")));
        _ = signals.Should().BeEmpty();
    }

    [Fact]
    public void SelectLaneSignals_completes_once_every_seen_target_has_a_signal()
    {
        var result = GitHubOrgClient.SelectLaneSignals(
            "CI",
            "https://github.com/FixPortal/repo",
            [
                Run(
                    170,
                    "completed",
                    Job("Deploy (centerprise-dev) / Deploy (centerprise-dev)", "completed", "success"),
                    Job("Deploy (fixportal-prod) / Deploy (fixportal-prod)", "completed", "success")
                ),
            ],
            DefaultPatterns
        );

        _ = result.Complete.Should().BeTrue();
    }

    [Fact]
    public void SelectLaneSignals_not_complete_while_a_seen_target_is_still_skipped()
    {
        var result = GitHubOrgClient.SelectLaneSignals(
            "CI",
            "https://github.com/FixPortal/repo",
            [
                Run(
                    170,
                    "completed",
                    Job("Deploy (centerprise-dev) / Deploy (centerprise-dev)", "completed", "success"),
                    Job("Deploy (fixportal-prod)", "completed", "skipped")
                ),
            ],
            DefaultPatterns
        );

        _ = result.Complete.Should().BeFalse();
    }

    [Fact]
    public void SelectLaneSignals_completes_on_a_completed_non_deploy_run_but_not_an_in_progress_one()
    {
        var noJobs = new[] { Job("Backend (.NET)", "completed", "success") };
        _ = GitHubOrgClient
            .SelectLaneSignals("CI", "u", [Run(1, "completed", noJobs)], DefaultPatterns)
            .Complete.Should()
            .BeTrue();
        _ = GitHubOrgClient
            .SelectLaneSignals("CI", "u", [Run(1, "in_progress", noJobs)], DefaultPatterns)
            .Complete.Should()
            .BeFalse();
    }
}
