using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// CB-H3: DashboardRefreshService is never actually constructed by any existing test
// — DashboardRefreshServiceTests.cs (470 lines) is entirely over its static helpers.
// This drives the real RefreshAsync over a real GitHubOrgClient wired to a fake
// HttpMessageHandler, to pin the MaxParallelRepos=6 gate and the rate-limit
// cancellation cascade (DashboardRefreshService.cs:23, 80-139).
public class DashboardRefreshServiceRefreshAsyncTests
{
    // Tracks the maximum number of repos concurrently past the semaphore gate (i.e.
    // mid per-repo HTTP call) at any point during the refresh.
    private sealed class ConcurrencyProbeHandler : HttpMessageHandler
    {
        public const int RepoCount = 8;

        // MaxParallelRepos gate under test (DashboardRefreshService.cs:23).
        private const int MaxParallel = 6;

        private int _inFlight;
        private int _maxObserved;
        private int _rateLimiterClaimed;

        // Completes when the cap is concurrently in flight, so the peak is observed
        // deterministically instead of depending on Task.Delay windows lining up.
        private readonly TaskCompletionSource _capReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxObserved => _maxObserved;

        private static string ReposJson =>
            "["
            + string.Join(
                ",",
                Enumerable
                    .Range(0, RepoCount)
                    .Select(i =>
                        $$"""{"name":"repo-{{i}}","html_url":"https://github.com/FixPortal/repo-{{i}}","private":false,"archived":false,"default_branch":"main"}"""
                    )
            )
            + "]";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/orgs/", StringComparison.Ordinal) && path.EndsWith("/repos", StringComparison.Ordinal))
            {
                return JsonOk(ReposJson);
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                path,
                "^/repos/[^/]+/(?<repo>[^/]+)/actions/workflows$"
            );
            if (match.Success)
            {
                var current = Interlocked.Increment(ref _inFlight);
                InterlockedMax(ref _maxObserved, current);
                try
                {
                    // Rendezvous: every gate-holder parks here until the cap is
                    // concurrently in flight. The semaphore admits at most MaxParallel,
                    // so all of them park together and the peak is exactly the cap —
                    // no reliance on the scheduler racing fixed delays.
                    if (current >= MaxParallel)
                    {
                        _capReached.TrySetResult();
                    }

                    await _capReached.Task.WaitAsync(cancellationToken);

                    // Whichever admitted request wins this race emits the 429 that fires
                    // the rate-limit cascade — it does not matter which repo, only that
                    // one of the rendezvoused requests does it, so semaphore-admission
                    // order can never leave the batch without a rate-limiter (no hang).
                    if (Interlocked.Exchange(ref _rateLimiterClaimed, 1) == 0)
                    {
                        return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    }

                    // The rest block until the cascade cancels them, so the
                    // sibling-cancellation is event-driven rather than a raced delay.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new OperationCanceledException(cancellationToken);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _inFlight);
                }
            }

            if (
                path.Contains("/actions/workflows/", StringComparison.Ordinal)
                && path.EndsWith("/runs", StringComparison.Ordinal)
            )
            {
                return JsonOk("""{"workflow_runs":[]}""");
            }

            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                return JsonOk("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int initial;
            do
            {
                initial = target;
                if (value <= initial)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref target, value, initial) != initial);
        }

        private static HttpResponseMessage JsonOk(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class RepoAuthFailureHandler : HttpMessageHandler
    {
        private const int RepoCount = 3;

        private int _workflowRequests;
        private readonly TaskCompletionSource _allWorkflowRequestsArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _authResponseConsumed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/orgs/", StringComparison.Ordinal) && path.EndsWith("/repos", StringComparison.Ordinal))
            {
                return JsonOk(
                    """
                    [
                      {"name":"auth-fails","html_url":"https://github.com/FixPortal/auth-fails","private":false,"archived":false,"default_branch":"main"},
                      {"name":"healthy-a","html_url":"https://github.com/FixPortal/healthy-a","private":false,"archived":false,"default_branch":"main"},
                      {"name":"healthy-b","html_url":"https://github.com/FixPortal/healthy-b","private":false,"archived":false,"default_branch":"main"}
                    ]
                    """
                );
            }

            if (path.EndsWith("/actions/workflows", StringComparison.Ordinal))
            {
                var repo = RepoName(path);
                if (Interlocked.Increment(ref _workflowRequests) == RepoCount)
                {
                    _allWorkflowRequestsArrived.TrySetResult();
                }

                await _allWorkflowRequestsArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (repo == "auth-fails")
                {
                    return new SignallingResponse(HttpStatusCode.Unauthorized, _authResponseConsumed);
                }

                // The auth response is disposed only after GitHubOrgClient has
                // populated LastAuthError, so these successes deterministically
                // complete later and expose any success-side race-clear.
                await _authResponseConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return JsonOk(
                    $$"""{"workflows":[{"id":1,"name":"{{repo}} CI","path":".github/workflows/{{repo}}.yml","state":"active"}]}"""
                );
            }

            if (
                path.Contains("/actions/workflows/", StringComparison.Ordinal)
                && path.EndsWith("/runs", StringComparison.Ordinal)
            )
            {
                var repo = RepoName(path);
                return JsonOk(
                    $$"""{"workflow_runs":[{"status":"completed","conclusion":"success","html_url":"https://github.com/FixPortal/{{repo}}/actions/runs/1","display_title":"{{repo}} fresh run","run_number":1,"head_branch":"main","event":"push","updated_at":"2025-12-31T23:30:00Z","id":1}]}"""
                );
            }

            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                return JsonOk("[]");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string RepoName(string path) => path.Split('/')[3];

        private static HttpResponseMessage JsonOk(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private sealed class SignallingResponse(HttpStatusCode statusCode, TaskCompletionSource disposed)
            : HttpResponseMessage(statusCode)
        {
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing)
                {
                    disposed.TrySetResult();
                }
            }
        }
    }

    // One repo, no workflows, one open PR. Just enough for RefreshAsync to publish a
    // snapshot whose pull requests can be inspected.
    private sealed class OnePullRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                _ when path.EndsWith("/repos", StringComparison.Ordinal) =>
                    """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]""",
                _ when path.EndsWith("/actions/workflows", StringComparison.Ordinal) => """{"workflows":[]}""",
                _ when path.EndsWith("/pulls", StringComparison.Ordinal) =>
                    """[{"number":181,"title":"Add widget","user":{"login":"chris"},"html_url":"https://github.com/FixPortal/repo-a/pull/181","draft":false,"created_at":"2026-07-30T09:00:00Z"}]""",
                _ => "[]",
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    [Fact]
    public async Task RefreshAsync_should_attach_cached_review_signals_to_the_published_snapshot()
    {
        // The only place a review signal reaches a published snapshot is the
        // TryGet + ApplyReviewSignals pair in CollectRepoAsync. Both other RefreshAsync
        // tests pass an empty cache, so deleting those two lines left the suite green.
        var state = new DashboardSnapshotState();
        using var handler = new OnePullRequestHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.github.com/");
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var signals = new ReviewSignal[] { new("Gitar", ReviewSignalState.Clean, null, null) };
        var reviewSignalCache = new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>();
        reviewSignalCache.Update("repo-a", new Dictionary<int, IReadOnlyList<ReviewSignal>> { [181] = signals });
        var sut = new DashboardRefreshService(
            client,
            new GitHubInventoryCache(client, clock, dashboardOptions),
            Substitute.For<IDashboardSnapshotStore>(),
            state,
            new PerRepoCache<RepoMetrics>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<MergedPullRequest>(),
            reviewSignalCache,
            gitHubOptions,
            clock,
            NullLogger<DashboardRefreshService>.Instance
        );

        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        _ = state.Current.Should().NotBeNull();
        var pr = state
            .Current!.Repositories.Should()
            .ContainSingle()
            .Which.PullRequests.Should()
            .ContainSingle()
            .Subject;
        _ = pr.Number.Should().Be(181);
        _ = pr.ReviewSignals.Should().BeEquivalentTo(signals);
    }

    [Fact]
    public async Task RefreshAsync_should_hold_the_concurrency_cap_and_cancel_siblings_on_rate_limit()
    {
        var handler = new ConcurrencyProbeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var inventory = new GitHubInventoryCache(client, clock, dashboardOptions);
        var state = new DashboardSnapshotState();
        var sut = new DashboardRefreshService(
            client,
            inventory,
            Substitute.For<IDashboardSnapshotStore>(),
            state,
            new PerRepoCache<RepoMetrics>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<MergedPullRequest>(),
            new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(),
            gitHubOptions,
            clock,
            NullLogger<DashboardRefreshService>.Instance
        );

        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        // Cap held: MaxParallelRepos is 6, and with 8 repos in flight this must be
        // observed exactly at 6 to prove real concurrency was exercised, not merely
        // that it never happened to exceed a trivially-true bound.
        _ = handler.MaxObserved.Should().Be(6);

        // Siblings cancelled: the rate-limited repo aborts the batch, so every repo —
        // including the ones still queued on the semaphore and the ones mid-flight —
        // ends up degraded (empty Workflows) rather than completing normally. A
        // regression that drops the cascade would let at least the non-gated repos
        // (those never delayed past the rate limit) complete successfully instead.
        _ = state.Current.Should().NotBeNull();
        _ = state.Current!.Repositories.Should().HaveCount(ConcurrencyProbeHandler.RepoCount);
        _ = state.Current.Repositories.Should().OnlyContain(r => r.Workflows.Count == 0);
    }

    [Fact]
    public async Task RefreshAsync_should_isolate_repo_auth_failure_and_preserve_cycle_auth_error()
    {
        var state = new DashboardSnapshotState();
        using var handler = new RepoAuthFailureHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.github.com/");
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore(), state);
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var inventory = new GitHubInventoryCache(client, clock, dashboardOptions);
        var sut = new DashboardRefreshService(
            client,
            inventory,
            new FileDashboardSnapshotStore(Path.Join("TestResults", "unused-cib-4.json")),
            state,
            new PerRepoCache<RepoMetrics>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<MergedPullRequest>(),
            new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(),
            gitHubOptions,
            clock,
            NullLogger<DashboardRefreshService>.Instance
        );

        await sut.RefreshAsync(TestContext.Current.CancellationToken);

        _ = state.Current.Should().NotBeNull();
        var repos = state.Current!.Repositories.ToDictionary(r => r.Name);
        _ = repos.Should().HaveCount(3);
        _ = repos["healthy-a"]
            .Workflows.Should()
            .ContainSingle(w =>
                w.Name == "healthy-a CI"
                && w.State == SignalState.Success
                && w.LastRun != null
                && w.LastRun.Repository == "healthy-a"
            );
        _ = repos["healthy-b"]
            .Workflows.Should()
            .ContainSingle(w =>
                w.Name == "healthy-b CI"
                && w.State == SignalState.Success
                && w.LastRun != null
                && w.LastRun.Repository == "healthy-b"
            );
        _ = repos["auth-fails"].Workflows.Should().BeEmpty();
        _ = state.LastAuthError.Should().Contain("auth-fails");
    }
}
