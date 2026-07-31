using System.Net;
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
using NSubstitute;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// CB-H4: DashboardRefreshWorker's top-level crash-loop guard is never driven by any
// test. DashboardRefreshServiceTests.cs *proves* RefreshAsync can throw (IOException,
// OperationCanceledException — see PersistAndPublishAsync tests) but nothing asserts
// the worker actually absorbs a thrown exception instead of letting it kill the
// hosted service silently.
public class DashboardRefreshWorkerTests
{
    // Captures ILogger.LogError calls so the test can wait, event-driven, for the
    // exact moment RefreshSafelyAsync's catch clause has run — rather than sleeping
    // an arbitrary "should be long enough" duration.
    private sealed class SignalingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource ErrorLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (logLevel == LogLevel.Error)
            {
                ErrorLogged.TrySetResult();
            }
        }
    }

    // Always returns invalid JSON for the repos list, so DashboardRefreshService.
    // RefreshAsync's very first call (inventory.GetRepositoriesAsync) throws a
    // JsonException that RefreshAsync itself does not catch — exactly the
    // non-cancellation failure the worker's guard exists to absorb.
    private sealed class BrokenReposHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not valid json", Encoding.UTF8, "application/json"),
                }
            );
    }

    private static DashboardRefreshService NewBrokenRefreshService(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var inventory = new GitHubInventoryCache(client, clock, dashboardOptions);
        return new DashboardRefreshService(
            client,
            inventory,
            Substitute.For<IDashboardSnapshotStore>(),
            new DashboardSnapshotState(),
            new PerRepoCache<RepoMetrics>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<MergedPullRequest>(),
            new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(),
            gitHubOptions,
            clock,
            NullLoggerFor<DashboardRefreshService>()
        );
    }

    private static ILogger<T> NullLoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    [Fact]
    public async Task ExecuteAsync_should_survive_a_non_cancellation_refresh_failure_and_stop_cleanly_on_shutdown()
    {
        var refreshService = NewBrokenRefreshService(new BrokenReposHandler());
        var options = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var workerLogger = new SignalingLogger<DashboardRefreshWorker>();
        var worker = new DashboardRefreshWorker(refreshService, options, workerLogger);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        // Event-driven: wait for the exact moment the guard logged
        // the failed cycle, rather than sleeping a guessed duration.
        await workerLogger.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Survives: the background loop must still be alive (not faulted) right after
        // absorbing the failure — it is now waiting on the next PeriodicTimer tick.
        _ = worker.ExecuteTask.Should().NotBeNull();
        _ = worker.ExecuteTask!.IsFaulted.Should().BeFalse();
        _ = worker.ExecuteTask.IsCompleted.Should().BeFalse();

        // Stops on shutdown: StopAsync must unwind the loop without the task faulting.
        await worker
            .StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _ = worker.ExecuteTask.IsCompleted.Should().BeTrue();
        _ = worker.ExecuteTask.IsFaulted.Should().BeFalse();
    }
}
