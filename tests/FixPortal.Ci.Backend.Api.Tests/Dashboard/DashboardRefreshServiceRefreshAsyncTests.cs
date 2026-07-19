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
        public const string RateLimitedRepo = "repo-0";
        public const int RepoCount = 8;

        private int _inFlight;
        private int _maxObserved;

        public int MaxObserved => _maxObserved;

        public static string ReposJson => "[" + string.Join(",", Enumerable.Range(0, RepoCount).Select(i =>
            $$"""{"name":"repo-{{i}}","html_url":"https://github.com/FixPortal/repo-{{i}}","private":false,"archived":false,"default_branch":"main"}""")) + "]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/orgs/", StringComparison.Ordinal) && path.EndsWith("/repos", StringComparison.Ordinal))
            {
                return JsonOk(ReposJson);
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                path, @"^/repos/[^/]+/(?<repo>[^/]+)/actions/workflows$");
            if (match.Success)
            {
                var repo = match.Groups["repo"].Value;
                var current = Interlocked.Increment(ref _inFlight);
                InterlockedMax(ref _maxObserved, current);
                try
                {
                    if (repo == RateLimitedRepo)
                    {
                        // Resolves fast (relative to the other repos' delay below) so
                        // it reliably triggers the rate-limit cascade while the other
                        // gate-holders are still mid-flight, and while the queued
                        // repos are still blocked on the semaphore.
                        await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
                        return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                    return JsonOk("""{"workflows":[{"id":1,"name":"CI","path":".github/workflows/ci.yml","state":"active"}]}""");
                }
                finally
                {
                    _ = Interlocked.Decrement(ref _inFlight);
                }
            }

            if (path.Contains("/actions/workflows/", StringComparison.Ordinal) && path.EndsWith("/runs", StringComparison.Ordinal))
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

        private static HttpResponseMessage JsonOk(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
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
            gitHubOptions,
            clock,
            NullLogger<DashboardRefreshService>.Instance);

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
}
