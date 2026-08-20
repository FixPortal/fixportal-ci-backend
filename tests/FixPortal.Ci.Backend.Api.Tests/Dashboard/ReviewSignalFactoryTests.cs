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
    private const string RepoUrl = "https://github.com/FixPortal/repo";

    private static readonly ReviewerOptions CodeRabbit = new()
    {
        Name = "CodeRabbit",
        BotLogin = "coderabbitai",
        RequiredLabel = "review-high",
    };

    private static readonly ReviewerOptions Gitar = new() { Name = "Gitar", BotLogin = "gitar-app" };

    private static readonly ReviewerOptions GitarWithComments = new()
    {
        Name = "Gitar",
        BotLogin = "gitar-app",
        CommentsCountAsParticipation = true,
    };

    private static readonly ReviewerOptions CodeQl = new() { Name = "CodeQL", Source = ReviewerSource.CodeScanning };

    private static readonly ReviewerOptions SecretScanning = new()
    {
        Name = "Secret Scanning",
        Source = ReviewerSource.SecretScanning,
    };

    private static PrReviewFacts Facts(
        IEnumerable<string>? labels = null,
        IDictionary<string, int>? unresolved = null,
        IEnumerable<string>? headParticipating = null,
        IEnumerable<string>? checkApps = null,
        IEnumerable<string>? headComments = null,
        IEnumerable<string>? truncated = null
    ) =>
        new(
            181,
            "chris",
            new HashSet<string>(labels ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(unresolved ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(headParticipating ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(headComments ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(checkApps ?? [], StringComparer.OrdinalIgnoreCase),
            HeadSha: "head-sha",
            TruncatedConnections: truncated is null ? null : new HashSet<string>(truncated, StringComparer.Ordinal)
        );

    private static ReviewSignal Only(
        ReviewerOptions reviewer,
        PrReviewFacts facts,
        int? openAlerts = null,
        int? openSecretAlerts = null
    ) => ReviewSignalFactory.Build(facts, [reviewer], openAlerts, openSecretAlerts, PrUrl, RepoUrl)[0];

    [Fact]
    public void Disabled_when_the_required_label_is_absent()
    {
        var signal = Only(CodeRabbit, Facts());

        _ = signal.State.Should().Be(ReviewSignalState.Disabled);
        _ = signal.Count.Should().BeNull();
        _ = signal.HtmlUrl.Should().BeNull();
    }

    [Fact]
    public void Disabled_outranks_outstanding_so_an_unrequired_reviewer_never_shows_red()
    {
        var facts = Facts(unresolved: new Dictionary<string, int> { ["coderabbitai"] = 4 });

        _ = Only(CodeRabbit, facts).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Disabled_outranks_open_alerts_on_the_code_scanning_source()
    {
        var gated = new ReviewerOptions
        {
            Name = "CodeQL",
            Source = ReviewerSource.CodeScanning,
            RequiredLabel = "review-high",
        };

        _ = Only(gated, Facts(checkApps: ["github-advanced-security"]), 2)
            .State.Should()
            .Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Whitespace_required_label_does_not_permanently_disable_the_reviewer()
    {
        // A misconfigured RequiredLabel of "  " could never match a real label (CollectLabels
        // skips whitespace-only names), so treat it as unset rather than an unmatchable gate.
        var whitespaceGated = new ReviewerOptions
        {
            Name = "Weird",
            BotLogin = "weird-app",
            RequiredLabel = "   ",
        };

        _ = Only(whitespaceGated, Facts(headParticipating: ["weird-app"])).State.Should().Be(ReviewSignalState.Clean);
    }

    [Theory]
    [InlineData("review-high ")]
    [InlineData(" review-high")]
    public void A_required_label_with_stray_whitespace_still_matches_rather_than_disabling_forever(string configured)
    {
        // Untrimmed, "review-high " matches no real label, so the reviewer reads Disabled
        // on every PR — and a human reads Disabled as "safe to skip", not "misconfigured".
        var padded = new ReviewerOptions
        {
            Name = "CodeRabbit",
            BotLogin = "coderabbitai",
            RequiredLabel = configured,
        };

        var signal = Only(padded, Facts(labels: ["review-high"], headParticipating: ["coderabbitai"]));

        _ = signal.State.Should().Be(ReviewSignalState.Clean);
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
        var signal = Only(Gitar, Facts(headParticipating: ["gitar-app"]));

        _ = signal.State.Should().Be(ReviewSignalState.Clean);
        _ = signal.Count.Should().BeNull();
        _ = signal.HtmlUrl.Should().BeNull();
    }

    [Fact]
    public void A_successful_check_alone_is_not_evidence_a_thread_reviewer_ran()
    {
        // CodeRabbit's "rate limited" / "review skipped" checks pass by design so they
        // never block a protected-branch merge. A passing check from the bot's app slug
        // must not read as Clean -- only actual participation earns that.
        _ = Only(Gitar, Facts(checkApps: ["gitar-app"])).State.Should().Be(ReviewSignalState.Pending);
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
    public void Code_scanning_state_follows_the_open_alert_count_when_a_scan_has_run(
        int alerts,
        ReviewSignalState expected
    )
    {
        var facts = Facts(checkApps: ["github-advanced-security"]);
        var expectedCount = alerts > 0 ? alerts : (int?)null;
        var expectedUrl = alerts > 0 ? $"{PrUrl}/checks" : null;

        _ = Only(CodeQl, facts, alerts).Should().Be(new ReviewSignal("CodeQL", expected, expectedCount, expectedUrl));
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
        _ = Only(CodeQl, Facts(checkApps: ["github-advanced-security"])).State.Should().Be(ReviewSignalState.Pending);
    }

    [Theory]
    // Pins the real-world value AND the compatibility value: a test that asserted the magic
    // string against the same magic string baked into the factory would prove only that
    // someone typed it twice. "github-advanced-security" is what GitHub actually emits for
    // CodeQL check runs today (verified 2026-08-03 by GraphQL against live PRs); the previous
    // fixtures asserted the historical "github-code-scanning" slug against itself, which is
    // exactly how the permanent-Pending bug shipped with a fully green test suite.
    [InlineData("github-advanced-security")]
    [InlineData("github-code-scanning")]
    public void Zero_alerts_is_clean_for_either_accepted_code_scanning_app_slug(string appSlug)
    {
        _ = Only(CodeQl, Facts(checkApps: [appSlug]), 0).State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void Builds_one_signal_per_configured_reviewer_in_configuration_order()
    {
        var signals = ReviewSignalFactory.Build(Facts(), [CodeRabbit, Gitar, CodeQl], null, null, PrUrl, RepoUrl);

        _ = signals.Select(s => s.Name).Should().Equal("CodeRabbit", "Gitar", "CodeQL");
    }

    [Theory]
    // Unreadable stays unknown: on a repository where the product is off, GitHub 404s the
    // route and the count arrives null. Reading that as Clean would put a green pill on a
    // repository nothing has ever scanned.
    [InlineData(null, ReviewSignalState.Pending)]
    [InlineData(0, ReviewSignalState.Clean)]
    [InlineData(3, ReviewSignalState.Outstanding)]
    public void Secret_scanning_maps_its_alert_count_to_a_state(int? openSecretAlerts, ReviewSignalState expected)
    {
        _ = Only(SecretScanning, Facts(), openSecretAlerts: openSecretAlerts).State.Should().Be(expected);
    }

    [Fact]
    public void Secret_scanning_links_to_the_repository_alert_list_not_the_pull_request()
    {
        // Repo-scoped by nature -- the alerts route takes no ref filter -- so the link must
        // land where the alert actually lives, not on a pull request that merely inherits it.
        var signal = Only(SecretScanning, Facts(), openSecretAlerts: 2);

        _ = signal.Count.Should().Be(2);
        _ = signal.HtmlUrl.Should().Be($"{RepoUrl}/security/secret-scanning");
    }

    [Fact]
    public void Secret_scanning_needs_no_per_pull_request_run_to_report_clean()
    {
        // Unlike code scanning, which waits for a completed check on this head: secret
        // scanning is continuous, so the endpoint answering IS the evidence it ran. Facts
        // carry no check apps here on purpose.
        _ = Only(SecretScanning, Facts(), openSecretAlerts: 0).State.Should().Be(ReviewSignalState.Clean);
    }

    private static readonly ReviewerOptions CodeQuality = new()
    {
        Name = "Code Quality",
        BotLogin = "github-code-quality",
        CodeScanningCheckCountsAsParticipation = true,
    };

    [Fact]
    public void A_silent_reviewer_delivered_by_the_scan_pipeline_is_clean_once_the_scan_check_passes()
    {
        // Code Quality publishes findings as review threads but says NOTHING when it finds
        // nothing, so no review, thread or comment exists on a clean pull request. The
        // successful code-scanning check is the only evidence available that it ran.
        _ = Only(CodeQuality, Facts(checkApps: ["github-advanced-security"]))
            .State.Should()
            .Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void The_scan_check_channel_does_not_fire_without_the_opt_in()
    {
        var withoutFlag = new ReviewerOptions { Name = "Code Quality", BotLogin = "github-code-quality" };

        _ = Only(withoutFlag, Facts(checkApps: ["github-advanced-security"]))
            .State.Should()
            .Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void A_passing_scan_check_never_masks_an_open_code_quality_finding()
    {
        // The regression that matters: the check says "the scan ran", never "the scan was
        // happy". Unresolved threads are decided first and must still win, or a green
        // pipeline would turn every outstanding finding into a clean pill.
        var facts = Facts(
            unresolved: new Dictionary<string, int> { ["github-code-quality"] = 2 },
            checkApps: ["github-advanced-security"]
        );

        var signal = Only(CodeQuality, facts);

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.Count.Should().Be(2);
    }

    [Fact]
    public void A_reviewer_with_no_scan_check_on_this_head_stays_pending()
    {
        _ = Only(CodeQuality, Facts()).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void A_review_threads_reviewer_with_no_bot_login_is_pending_rather_than_falsely_clean()
    {
        var misconfigured = new ReviewerOptions { Name = "Mystery" };

        _ = Only(misconfigured, Facts(headParticipating: ["someone"])).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void A_head_comment_is_ignored_unless_the_reviewer_opted_in()
    {
        // Gitar has the flag OFF. Comments must not silently become evidence for every
        // reviewer -- CodeRabbit posts chatty status comments it never intends as a pass.
        _ = Only(Gitar, Facts(headComments: ["gitar-app"])).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Clean_when_an_opted_in_reviewer_left_a_head_comment_and_nothing_unresolved()
    {
        // The whole point: Gitar announces "no issues found" as an issue comment, submits
        // no review and opens no thread, and must still read Clean.
        var signal = Only(GitarWithComments, Facts(headComments: ["gitar-app"]));

        _ = signal.State.Should().Be(ReviewSignalState.Clean);
        _ = signal.Count.Should().BeNull();
        _ = signal.HtmlUrl.Should().BeNull();
    }

    [Fact]
    public void Unresolved_threads_still_outrank_an_opted_in_head_comment()
    {
        // Gitar opens threads for findings AND comments a summary. Outstanding must win,
        // or a finding would be masked by the very comment that reported it.
        var facts = Facts(unresolved: new Dictionary<string, int> { ["gitar-app"] = 2 }, headComments: ["gitar-app"]);

        var signal = Only(GitarWithComments, facts);

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.Count.Should().Be(2);
    }

    // M1: a reviewThreads connection past its page cap is a lower bound, not a count —
    // an unresolved thread beyond item 100 is simply absent. Participation evidence must
    // not promote such a pull request to Clean.
    [Fact]
    public void A_truncated_thread_connection_holds_the_reviewer_at_pending_not_clean()
    {
        var signal = Only(
            GitarWithComments,
            Facts(headParticipating: ["gitar-app"], truncated: [GitHubOrgClient.ReviewThreadsConnectionName])
        );

        _ = signal.State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void A_truncated_thread_connection_does_not_hide_known_unresolved_threads()
    {
        // Positive evidence of open findings still reports Outstanding; only the Clean
        // direction is suppressed.
        var signal = Only(
            GitarWithComments,
            Facts(
                unresolved: new Dictionary<string, int> { ["gitar-app"] = 1 },
                truncated: [GitHubOrgClient.ReviewThreadsConnectionName]
            )
        );

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
    }

    [Fact]
    public void Another_bots_head_comment_does_not_make_this_reviewer_clean()
    {
        _ = Only(GitarWithComments, Facts(headComments: ["coderabbitai"])).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void An_opted_in_reviewer_still_respects_the_required_label_gate()
    {
        var gated = new ReviewerOptions
        {
            Name = "Gitar",
            BotLogin = "gitar-app",
            RequiredLabel = "review-high",
            CommentsCountAsParticipation = true,
        };

        _ = Only(gated, Facts(headComments: ["gitar-app"])).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Comment_participation_does_not_apply_to_a_code_scanning_reviewer()
    {
        // BuildCodeScanning is a separate path; the flag must not leak into it.
        var scanning = new ReviewerOptions
        {
            Name = "CodeQL",
            Source = ReviewerSource.CodeScanning,
            BotLogin = "github-code-scanning",
            CommentsCountAsParticipation = true,
        };

        _ = Only(scanning, Facts(headComments: ["github-code-scanning"]), 0)
            .State.Should()
            .Be(ReviewSignalState.Pending);
    }
}
