using System.Reflection;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class LizardScannerTests
{
    [Fact]
    public void BuildCloneCommand_keeps_the_token_out_of_git_arguments()
    {
        var (arguments, environment) = LizardScanner.BuildCloneCommand(
            "FixPortal",
            "fixportal-ci-dashboard",
            "super-secret-token",
            "C:\\temp\\repo"
        );

        _ = string.Join(' ', arguments).Should().NotContain("super-secret-token");
        _ = arguments.Should().Contain("https://github.com/FixPortal/fixportal-ci-dashboard.git");
        _ = environment["GIT_CONFIG_KEY_0"].Should().Be("http.https://github.com/.extraheader");
        _ = environment["GIT_CONFIG_VALUE_0"]
            .Should()
            .Be($"AUTHORIZATION: basic {Convert.ToBase64String("x-access-token:super-secret-token"u8)}");
    }

    private static LizardScanner NewScanner(string workRoot) =>
        new(
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(
                new DashboardOptions
                {
                    SnapshotPath = "s.json",
                    RefreshSeconds = 60,
                    MetricsWorkDirectory = workRoot,
                }
            ),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<LizardScanner>.Instance
        );

    // CB-C4: the guard must reject before any process spawn or delete when a repo
    // name (sourced from the semi-trusted GitHub API) is rooted. A null return alone
    // does not prove the guard fired — both the guarded and an unguarded path can
    // return null — so this only pins the rooted-name branch; the traversal test
    // below is the one that proves harm is actually prevented.
    [Fact]
    public async Task ScanAsync_should_return_null_for_a_rooted_repo_name()
    {
        var scanner = NewScanner(Path.Combine(Path.GetTempPath(), $"ci-scanner-workroot-{Guid.NewGuid():N}"));

        var result = await scanner.ScanAsync(Path.GetTempPath(), TestContext.Current.CancellationToken);

        _ = result.Should().BeNull();
    }

    // CB-C4: the real regression to guard against is a weakened containment check
    // letting ScanAsync's TryDeleteDir(dir) delete something outside WorkRoot. A
    // sentinel directory placed OUTSIDE WorkRoot, reached via a repo name that
    // resolves out of it (".." traversal), must survive the call untouched — the
    // observable harm is the delete, not the return value.
    [Fact]
    public async Task ScanAsync_should_not_touch_a_directory_outside_WorkRoot_when_the_repo_name_traverses_out()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), $"ci-scanner-workroot-{Guid.NewGuid():N}");
        var sentinelDir = Path.Combine(Path.GetTempPath(), $"ci-scanner-sentinel-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(sentinelDir);
        var markerFile = Path.Combine(sentinelDir, "marker.txt");
        await File.WriteAllTextAsync(markerFile, "do-not-delete", TestContext.Current.CancellationToken);
        try
        {
            var scanner = NewScanner(workRoot);
            // WorkRoot's own directory need not exist yet — repo name traverses one
            // level up from it (relative to WorkRoot's parent, the temp dir) and back
            // into the sentinel directory sitting alongside it.
            var repoName = $"../{Path.GetFileName(sentinelDir)}";

            var result = await scanner.ScanAsync(repoName, TestContext.Current.CancellationToken);

            _ = result.Should().BeNull();
            _ = Directory.Exists(sentinelDir).Should().BeTrue();
            _ = File.Exists(markerFile).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(sentinelDir, recursive: true);
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    // CB-H2: Scrub is private and reached only from the log sites inside the
    // untested ScanAsync (git-clone-failure and lizard-unparseable branches).
    // Faking a cross-platform "git"/"lizard" binary via PATH just to exercise those
    // two log call sites would be far more fragile than the thing it proves; Scrub's
    // token-redaction behaviour is fully determined by GitHubOptions.Token and the
    // input string, so invoke the real (unmodified) method directly via reflection —
    // this exercises production logic, not a reimplementation of it.
    [Fact]
    public void Scrub_should_redact_the_configured_token_from_log_text()
    {
        const string token = "ghp_super-secret-pat-token-value";
        var scanner = new LizardScanner(
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = token }),
            Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 }),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<LizardScanner>.Instance
        );
        var scrub = typeof(LizardScanner).GetMethod("Scrub", BindingFlags.NonPublic | BindingFlags.Instance);
        _ = scrub.Should().NotBeNull("LizardScanner.Scrub must still exist as the redaction call site");

        var scrubbed = (string)scrub!.Invoke(scanner, [$"fatal: authentication failed using token {token}"])!;

        _ = scrubbed.Should().NotContain(token);
        _ = scrubbed.Should().Contain("***");
    }
}
