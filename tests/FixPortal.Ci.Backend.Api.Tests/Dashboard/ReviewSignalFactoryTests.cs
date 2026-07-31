using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalFactoryTests
{
    private const string PrUrl = "https://github.com/FixPortal/repo/pull/181";

    private static readonly ReviewerOptions CodeRabbit = new()
    {
        Name = "CodeRabbit",
        BotLogin = "coderabbitai",
        RequiredLabel = "review-high",
    };

    private static readonly ReviewerOptions Gitar = new() { Name = "Gitar", BotLogin = "gitar-app" };

    private static readonly ReviewerOptions CodeQl = new() { Name = "CodeQL", Source = ReviewerSource.CodeScanning };

    private static PrReviewFacts Facts(
        IEnumerable<string>? labels = null,
        IDictionary<string, int>? unresolved = null,
        IEnumerable<string>? participating = null,
        IEnumerable<string>? checkApps = null
    ) =>
        new(
            181,
            "chris",
            new HashSet<string>(labels ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(unresolved ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(participating ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(checkApps ?? [], StringComparer.OrdinalIgnoreCase)
        );

    private static ReviewSignal Only(ReviewerOptions reviewer, PrReviewFacts facts, int? openAlerts = null) =>
        ReviewSignalFactory.Build(facts, [reviewer], openAlerts, PrUrl)[0];

    [Fact]
    public void Disabled_when_the_required_label_is_absent()
    {
        _ = Only(CodeRabbit, Facts()).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Disabled_outranks_outstanding_so_an_unrequired_reviewer_never_shows_red()
    {
        var facts = Facts(unresolved: new Dictionary<string, int> { ["coderabbitai"] = 4 });

        _ = Only(CodeRabbit, facts).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Outstanding_with_a_count_when_the_bot_has_unresolved_threads()
    {
        var facts = Facts(labels: ["review-high"], unresolved: new Dictionary<string, int> { ["coderabbitai"] = 3 });

        var signal = Only(CodeRabbit, facts);

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.Count.Should().Be(3);
        _ = signal.HtmlUrl.Should().Be($"{PrUrl}/files");
    }

    [Fact]
    public void A_humans_unresolved_thread_is_not_the_bots_problem()
    {
        var facts = Facts(labels: ["review-high"], unresolved: new Dictionary<string, int> { ["chris"] = 2 });

        _ = Only(CodeRabbit, facts).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Clean_when_the_bot_participated_and_left_nothing_unresolved()
    {
        _ = Only(Gitar, Facts(participating: ["gitar-app"])).State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void Clean_when_the_only_evidence_is_a_successful_check_from_that_app()
    {
        _ = Only(Gitar, Facts(checkApps: ["gitar-app"])).State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void Pending_when_a_required_reviewer_is_simply_silent()
    {
        // The paused-Gitar case. Silence must never read as a pass.
        var signal = Only(Gitar, Facts());

        _ = signal.State.Should().Be(ReviewSignalState.Pending);
        _ = signal.HtmlUrl.Should().BeNull();
        _ = signal.Count.Should().BeNull();
    }

    [Theory]
    [InlineData(2, ReviewSignalState.Outstanding)]
    [InlineData(0, ReviewSignalState.Clean)]
    public void Code_scanning_state_follows_the_open_alert_count_when_a_scan_has_run(int alerts, ReviewSignalState expected)
    {
        var facts = Facts(checkApps: ["github-code-scanning"]);

        _ = Only(CodeQl, facts, alerts).State.Should().Be(expected);
    }

    [Fact]
    public void Code_scanning_is_pending_when_no_scan_has_run_even_with_zero_alerts()
    {
        _ = Only(CodeQl, Facts(), 0).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Code_scanning_is_pending_when_alerts_could_not_be_read()
    {
        // null openAlerts = endpoint unavailable. Must not render as a clean scan.
        _ = Only(CodeQl, Facts(checkApps: ["github-code-scanning"]), null).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Builds_one_signal_per_configured_reviewer_in_configuration_order()
    {
        var signals = ReviewSignalFactory.Build(Facts(), [CodeRabbit, Gitar, CodeQl], null, PrUrl);

        _ = signals.Select(s => s.Name).Should().Equal("CodeRabbit", "Gitar", "CodeQL");
    }

    [Fact]
    public void A_review_threads_reviewer_with_no_bot_login_is_pending_rather_than_falsely_clean()
    {
        var misconfigured = new ReviewerOptions { Name = "Mystery" };

        _ = Only(misconfigured, Facts(participating: ["someone"])).State.Should().Be(ReviewSignalState.Pending);
    }
}
