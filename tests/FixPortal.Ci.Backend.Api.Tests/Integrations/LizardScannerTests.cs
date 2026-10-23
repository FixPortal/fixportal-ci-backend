using System.Reflection;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
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

    private static LizardScanner NewScanner(string workRoot, IGitHubTokenSource? tokenSource = null) =>
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
            tokenSource
                ?? new StaticGitHubTokenSource(Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" })),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<LizardScanner>.Instance
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_failure_returns_null_and_removes_the_owned_clone(bool timeout)
    {
        using var owned = new OwnedScannerDirectory();
        owned.CreateClone();
        var initialCleanupObserved = false;
        CancellationToken receivedToken = default;
        var tokens = new FailingTokenSource(ct =>
        {
            receivedToken = ct;
            initialCleanupObserved = !Directory.Exists(owned.Clone);
            owned.CreateClone();
            if (timeout)
            {
                throw new OperationCanceledException("token acquisition timed out without caller shutdown");
            }
            throw new InvalidOperationException("token acquisition failed");
        });

        RepoMetrics? result = null;
        Func<Task> act = async () =>
        {
            result = await NewScanner(owned.Root, tokens)
                .ScanAsync("owned-repo", TestContext.Current.CancellationToken);
        };
        _ = await act.Should()
            .NotThrowAsync("a token failure without caller shutdown must preserve last-known-good metrics");

        _ = tokens.Called.Should().BeTrue("failure must occur inside ScanAsync's try block before any child process");
        _ = receivedToken.Should().Be(TestContext.Current.CancellationToken);
        _ = initialCleanupObserved.Should().BeTrue("the initial cleanup must remove the old clone");
        _ = result.Should().BeNull();
        _ = Directory
            .Exists(owned.Clone)
            .Should()
            .BeFalse("finally must remove the clone recreated at the failure boundary");
        _ = Directory.Exists(owned.Root).Should().BeTrue("cleanup must remain scoped to the owned clone");
    }

    [Fact]
    public async Task Caller_shutdown_propagates_and_still_removes_the_owned_clone()
    {
        using var owned = new OwnedScannerDirectory();
        owned.CreateClone();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialCleanupObserved = false;
        CancellationToken receivedToken = default;
        var tokens = new FailingTokenSource(ct =>
        {
            receivedToken = ct;
            initialCleanupObserved = !Directory.Exists(owned.Clone);
            owned.CreateClone();
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        var scanner = NewScanner(owned.Root, tokens);

        var act = () => scanner.ScanAsync("owned-repo", cancellation.Token);
        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();

        _ = thrown.Which.CancellationToken.Should().Be(cancellation.Token);
        _ = tokens.Called.Should().BeTrue();
        _ = receivedToken.Should().Be(cancellation.Token);
        _ = initialCleanupObserved.Should().BeTrue();
        _ = Directory.Exists(owned.Clone).Should().BeFalse("shutdown must also execute the finally cleanup");
        _ = Directory.Exists(owned.Root).Should().BeTrue();
    }

    private sealed class FailingTokenSource(Action<CancellationToken> fail) : IGitHubTokenSource
    {
        public bool Called { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken ct)
        {
            Called = true;
            fail(ct);
            throw new InvalidOperationException("The test token source must fail before any git invocation.");
        }
    }

    private sealed class OwnedScannerDirectory : IDisposable
    {
        private readonly string _witness = Guid.NewGuid().ToString("N");
        private readonly string _resolvedRoot;
        public string Root => _resolvedRoot;
        public string Clone => Path.Combine(Root, "owned-repo");

        public OwnedScannerDirectory()
        {
            _resolvedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"lizard-failure-{_witness}"));
            if (Directory.Exists(_resolvedRoot))
            {
                throw new IOException("Refusing to claim an existing fixture directory.");
            }
            _ = Directory.CreateDirectory(_resolvedRoot);
            using var marker = new FileStream(Path.Combine(Root, ".fixture-owner"), FileMode.CreateNew);
            marker.Write(System.Text.Encoding.UTF8.GetBytes(_witness));
            // Failed construction never reaches Dispose, so no unvalidated cleanup is attempted.
        }

        public void CreateClone()
        {
            Console.WriteLine($"Owned clone fixture: {Path.GetFullPath(Clone)}");
            _ = Directory.CreateDirectory(Clone);
            File.WriteAllText(Path.Combine(Clone, ".clone-owner"), _witness);
        }

        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            var marker = Path.Combine(target, ".fixture-owner");
            if (target != _resolvedRoot || !File.Exists(marker) || File.ReadAllText(marker) != _witness)
            {
                Console.WriteLine("Refusing recursive cleanup without the exact fixture ownership witness.");
                return;
            }
            Console.WriteLine($"Deleting owned scanner fixture: {target}");
            Directory.Delete(target, recursive: true);
        }
    }

    // The regression to guard against is a weakened containment check
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

    // Scrub is private and reached only from the log sites inside the
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
        const string installationToken = "ghs_active-installation-secret";
        var scanner = new LizardScanner(
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = token }),
            Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 }),
            new StaticGitHubTokenSource(Options.Create(new GitHubOptions { Owner = "FixPortal", Token = token })),
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<LizardScanner>.Instance
        );
        var scrub = typeof(LizardScanner).GetMethod("Scrub", BindingFlags.NonPublic | BindingFlags.Instance);
        _ = scrub.Should().NotBeNull("LizardScanner.Scrub must still exist as the redaction call site");

        var scrubbed = (string)
            scrub.Invoke(
                scanner,
                [$"fatal: authentication failed using token {token}; active {installationToken}", installationToken]
            )!;

        _ = scrubbed.Should().NotContain(token);
        _ = scrubbed.Should().NotContain(installationToken);
        _ = scrubbed.Should().Contain("***");
    }
}
