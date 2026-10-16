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
public sealed class DashboardRefreshWorkerTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

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

    private static DashboardRefreshService NewRefreshService(
        HttpClient http,
        DashboardSnapshotState state,
        IDashboardSnapshotStore store
    )
    {
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var inventory = new GitHubInventoryCache(client, clock, dashboardOptions);
        return new DashboardRefreshService(
            client,
            inventory,
            store,
            state,
            new PerRepoCache<RepoMetrics>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<IReadOnlyList<JobSignal>>(),
            new PerRepoCache<MergedPullRequest>(),
            new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(),
            new PerRepoCache<IReadOnlyDictionary<int, PrMergeState>>(),
            Options.Create(new ReviewSignalsOptions()),
            gitHubOptions,
            clock,
            NullLoggerFor<DashboardRefreshService>()
        );
    }

    private static ILogger<T> NullLoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    [Fact]
    public async Task ExecuteAsync_should_survive_a_non_cancellation_refresh_failure_and_stop_cleanly_on_shutdown()
    {
        var http = new HttpClient(new BrokenReposHandler()) { BaseAddress = new Uri("https://api.github.com/") };
        _httpClients.Add(http);
        var refreshService = NewRefreshService(
            http,
            new DashboardSnapshotState(),
            Substitute.For<IDashboardSnapshotStore>()
        );
        var options = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var workerLogger = new SignalingLogger<DashboardRefreshWorker>();
        var worker = new DashboardRefreshWorker(refreshService, options, TimeProvider.System, workerLogger);

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

    private sealed class RecoveringReposHandler : HttpMessageHandler
    {
        private int _inventoryRequests;
        public int InventoryRequests => Volatile.Read(ref _inventoryRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = "[]";
            if (request.RequestUri!.AbsolutePath.EndsWith("/repos", StringComparison.Ordinal))
            {
                body =
                    Interlocked.Increment(ref _inventoryRequests) == 1
                        ? "not valid json"
                        : """[{"name":"recovered-repo","html_url":"https://github.com/FixPortal/recovered-repo","private":false,"archived":false,"default_branch":"main"}]""";
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/actions/workflows", StringComparison.Ordinal))
            {
                body = """{"workflows":[]}""";
            }
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    [Fact]
    public async Task ExecuteAsync_should_publish_a_successful_refresh_on_the_next_tick_after_initial_failure()
    {
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = ceiling.Token;
        var handler = new RecoveringReposHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var state = new DashboardSnapshotState();
        var store = Substitute.For<IDashboardSnapshotStore>();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DashboardSnapshot? savedSnapshot = null;
        var saveCount = 0;
        store
            .SaveAsync(Arg.Any<DashboardSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                savedSnapshot = call.Arg<DashboardSnapshot>();
                Interlocked.Increment(ref saveCount);
                saved.TrySetResult();
                return Task.CompletedTask;
            });
        var refreshService = NewRefreshService(http, state, store);
        var options = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var time = new TrackingFakeTimeProvider();
        var workerLogger = new SignalingLogger<DashboardRefreshWorker>();
        var worker = new DashboardRefreshWorker(refreshService, options, time, workerLogger);
        Exception? primaryFailure = null;
        try
        {
            await worker.StartAsync(ct);
            await workerLogger.ErrorLogged.Task.WaitAsync(ct);
            ct.IsCancellationRequested.Should().BeFalse("the operation deadline is diagnostic only");
            // Timer construction precedes the first refresh, whose real failure is
            // now observed. This is a completed-prefix assertion, not a timing poll.
            time.SteadyStateTimerScheduled.Task.IsCompletedSuccessfully.Should()
                .BeTrue("the scheduled refresh timer must bind the injected clock before the first failed refresh");
            await time.SteadyStateTimerScheduled.Task.WaitAsync(ct);
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.WhenAny(saved.Task, worker.ExecuteTask!).WaitAsync(ct);
            // Save signals before publication. Join the actual worker so the real
            // PersistAndPublishAsync continuation finishes state.Update first.
            await worker.StopAsync(CancellationToken.None).WaitAsync(ct);
            ct.IsCancellationRequested.Should().BeFalse("the operation deadline is diagnostic only");
            worker.ExecuteTask!.IsCompleted.Should().BeTrue();
            worker.ExecuteTask.IsFaulted.Should().BeFalse();
            state.Current.Should().NotBeNull("the next scheduled refresh must publish a successful snapshot");
            var recovered = state.Current!;
            recovered.Org.Should().Be("FixPortal");
            recovered.RefreshedAt.Should().Be(Instant.FromUtc(2026, 1, 1, 0, 0));
            recovered.Repositories.Should().ContainSingle().Which.Name.Should().Be("recovered-repo");
            savedSnapshot.Should().BeSameAs(recovered);
            handler.InventoryRequests.Should().Be(2);
            Volatile.Read(ref saveCount).Should().Be(1);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await worker.StopAsync(CancellationToken.None).WaitAsync(cleanup.Token);
                if (worker.ExecuteTask is { } execution)
                {
                    execution.IsCompleted.Should().BeTrue("worker shutdown must join before dependencies are disposed");
                }
                worker.Dispose();
                http.Dispose();
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                Console.WriteLine("ScheduledRecoveryCleanupFailure: " + cleanupError);
            }
        }
    }
}
