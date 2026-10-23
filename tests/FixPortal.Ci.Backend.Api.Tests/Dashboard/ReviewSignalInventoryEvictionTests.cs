using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public partial class ReviewSignalEnrichmentWorkerCollectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_removal_evicts_committed_state_and_head_only_soft_failure_orphans(bool failFirst)
    {
        await RunInventoryEvictionAsync(failFirst, inspectRetention: true);
    }

    [Fact]
    public async Task Returning_inventory_repository_refetches_unchanged_pr_instead_of_certifying_stale_clean_signals()
    {
        await RunInventoryEvictionAsync(failFirst: false, inspectRetention: false);
    }

    private static async Task RunInventoryEvictionAsync(bool failFirst, bool inspectRetention)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var handler = new InventoryEvictionHandler { FailAlerts = failFirst };
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "unused", RefreshSeconds = 20 });
        var github = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "test-only" });
        var client = new GitHubOrgClient(http, github, dashboard, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(clock, Duration.FromHours(1));
        var time = new TrackingFakeTimeProvider();
        var completed = new InventorySweeps();
        using var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, clock, dashboard),
            cache,
            Options.Create(
                new ReviewSignalsOptions
                {
                    Reviewers = [new() { Name = "CodeQL", Source = ReviewerSource.CodeScanning }],
                    RefreshSeconds = 60,
                }
            ),
            github,
            time,
            clock,
            completed
        );
        await worker.StartAsync(ct);
        try
        {
            await time.InitialDelayScheduled.Task.WaitAsync(ct);
            time.Advance(TimeSpan.FromSeconds(15));
            await time.SteadyStateTimerScheduled.Task.WaitAsync(ct);
            handler.GraphQlCalls.Should().Be(1);
            cache.TryGet(RepoName, out var first).Should().Be(!failFirst);
            if (!failFirst)
            {
                first![181].Signals.Single().State.Should().Be(ReviewSignalState.Clean);
            }
            if (inspectRetention)
            {
                StateMap(worker, "_watermarks").Contains(RepoName).Should().Be(!failFirst);
                StateMap(worker, "_observedAt").Contains(RepoName).Should().Be(!failFirst);
                StateMap(worker, "_headFirstSeenAt")
                    .Contains(RepoName)
                    .Should()
                    .BeTrue("the first real collect must populate the state whose eviction is under test");
            }

            handler.RepoPresent = false;
            clock.AdvanceSeconds(60);
            time.Advance(TimeSpan.FromSeconds(60));
            await completed.Second.Task.WaitAsync(ct);
            handler.InventoryCalls.Should().Be(2);
            handler.GraphQlCalls.Should().Be(1, "an absent repository must not be collected");
            if (inspectRetention)
            {
                foreach (var name in new[] { "_watermarks", "_observedAt", "_headFirstSeenAt" })
                {
                    StateMap(worker, name)
                        .Contains(RepoName)
                        .Should()
                        .BeFalse("{0} must not retain an inventory-removed repository", name);
                }
            }

            handler.RepoPresent = true;
            handler.FailAlerts = false;
            handler.SuccessfulCheck = false;
            clock.AdvanceSeconds(60);
            time.Advance(TimeSpan.FromSeconds(60));
            await completed.Third.Task.WaitAsync(ct);
            handler.InventoryCalls.Should().Be(3);
            handler
                .GraphQlCalls.Should()
                .Be(
                    2,
                    "returning inventory must start fresh even with an unchanged PR and a fresh cached clean signal"
                );
            cache.TryGet(RepoName, out var final).Should().BeTrue();
            final!.Keys.Should().Equal(181);
            final[181]
                .Signals.Single()
                .State.Should()
                .Be(ReviewSignalState.Pending, "the actual freshly fetched facts contain no successful CodeQL check");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Reflection is limited to the memory-retention contract: a head-only orphan has no
    // business-output difference after re-add because no committed watermark means a cold head.
    private static IDictionary StateMap(ReviewSignalEnrichmentWorker worker, string name) =>
        (IDictionary)
            typeof(ReviewSignalEnrichmentWorker)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(worker)!;

    private sealed class InventorySweeps : ILogger<ReviewSignalEnrichmentWorker>
    {
        private int _count;
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Third { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!formatter(state, exception).StartsWith("PR review signals sweep issued", StringComparison.Ordinal))
            {
                return;
            }
            var count = Interlocked.Increment(ref _count);
            if (count == 2)
            {
                Second.TrySetResult();
            }
            if (count == 3)
            {
                Third.TrySetResult();
            }
        }
    }

    private sealed class InventoryEvictionHandler : HttpMessageHandler
    {
        public bool RepoPresent { get; set; } = true;
        public bool FailAlerts { get; set; }
        public bool SuccessfulCheck { get; set; } = true;
        public int InventoryCalls { get; private set; }
        public int GraphQlCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var status = HttpStatusCode.OK;
            string body;
            if (path.EndsWith("/repos", StringComparison.Ordinal))
            {
                InventoryCalls++;
                body = RepoPresent
                    ? """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]"""
                    : "[]";
            }
            else if (path == "/graphql")
            {
                GraphQlCalls++;
                body = FactsJson("chris", SuccessfulCheck);
            }
            else if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                body = OpenPullsJson;
            }
            else if (path.Contains("code-scanning/alerts", StringComparison.Ordinal) && FailAlerts)
            {
                status = HttpStatusCode.ServiceUnavailable;
                body = "temporary scan failure";
            }
            else
            {
                body = "[]";
            }
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }
    }
}
