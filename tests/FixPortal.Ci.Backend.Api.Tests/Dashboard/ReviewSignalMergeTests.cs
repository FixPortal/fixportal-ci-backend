using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalMergeTests
{
    private static PullRequest Pr(int number) =>
        new(
            number,
            $"PR {number}",
            "chris",
            $"https://github.com/FixPortal/repo/pull/{number}",
            false,
            Instant.FromUnixTimeSeconds(1)
        );

    private static readonly IReadOnlyList<ReviewSignal> Signals = [new("Gitar", ReviewSignalState.Clean, null, null)];

    [Fact]
    public void Attaches_signals_to_the_matching_pull_request_only()
    {
        var merged = DashboardRefreshService.ApplyReviewSignals(
            [Pr(181), Pr(179)],
            new Dictionary<int, IReadOnlyList<ReviewSignal>> { [181] = Signals }
        );

        _ = merged[0].ReviewSignals.Should().BeEquivalentTo(Signals);
        _ = merged[1].ReviewSignals.Should().BeNull();
    }

    [Fact]
    public void Returns_the_original_list_untouched_when_there_are_no_cached_signals()
    {
        var prs = new[] { Pr(181) };

        var merged = DashboardRefreshService.ApplyReviewSignals(prs, null);

        _ = merged.Should().BeSameAs(prs);
    }

    [Fact]
    public void Leaves_every_other_field_of_the_pull_request_intact()
    {
        var merged = DashboardRefreshService.ApplyReviewSignals(
            [Pr(181)],
            new Dictionary<int, IReadOnlyList<ReviewSignal>> { [181] = Signals }
        );

        _ = merged[0].Number.Should().Be(181);
        _ = merged[0].Title.Should().Be("PR 181");
        _ = merged[0].Author.Should().Be("chris");
    }

    // I-2 (fix round 1): the cache Program.cs registers for review signals is given
    // a max-age, unlike its metrics/deploys/packages/merged-PR neighbours, so a
    // persistently-failing enrichment expires into "no pills" instead of a stale
    // Clean pass surviving indefinitely against a head no reviewer has seen.
    [Fact]
    public void An_expired_review_signal_cache_entry_reads_as_a_miss_so_the_snapshot_carries_no_signals()
    {
        var clock = new FakeClock(Instant.FromUnixTimeSeconds(1000));
        var cache = new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(
            clock,
            Duration.FromMinutes(10)
        );
        cache.Update("repo", new Dictionary<int, IReadOnlyList<ReviewSignal>> { [181] = Signals });

        clock.AdvanceMinutes(11);

        _ = cache.TryGet("repo", out var stale).Should().BeFalse();
        _ = stale.Should().BeNull();

        var merged = DashboardRefreshService.ApplyReviewSignals([Pr(181)], stale);
        _ = merged[0].ReviewSignals.Should().BeNull();
    }
}

public class ReviewSignalWorkerGatingTests
{
    // Serves the one-repo org listing so a sweep past the gate has something to
    // collect against; every other path answers an empty JSON array. Calls is
    // incremented with Interlocked, and RequestReceived fires on the first call,
    // because the positive-control test below awaits it from a different thread
    // than the fake-time-driven sweep that sets it.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            _ = Interlocked.Increment(ref _calls);
            RequestReceived.TrySetResult();
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/repos", StringComparison.Ordinal)
                ? """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]"""
                : "[]";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private static ReviewSignalEnrichmentWorker NewWorker(
        RecordingHandler handler,
        ReviewSignalsOptions options,
        TimeProvider timeProvider
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        return new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(),
            Options.Create(options),
            gitHubOptions,
            timeProvider,
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );
    }

    [Fact]
    public async Task Issues_no_requests_when_no_reviewers_are_configured()
    {
        var handler = new RecordingHandler();
        var worker = NewWorker(handler, new ReviewSignalsOptions(), new FakeTimeProvider());

        await worker.StartAsync(TestContext.Current.CancellationToken);
        // A disabled worker's ExecuteAsync returns immediately (Enabled is checked
        // before the jitter delay). If Enabled were wrongly true it would instead
        // park on WaitForInitialJitterAsync forever, so this await — with a bounded
        // timeout rather than an unbounded one — is what makes the test fail for the
        // right reason instead of vacuously passing on an un-awaited StartAsync. The
        // timeout is generous (30s, not a tight budget) because a disabled worker
        // returns synchronously and does not depend on scheduling latency; a stalled
        // runner should still report the real failure rather than a race.
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _ = worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _ = handler.Calls.Should().Be(0);

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Issues_requests_once_enabled_and_past_the_initial_jitter()
    {
        // Positive control: proves the harness can observe a real HTTP call when the
        // worker is genuinely enabled, so the negative test above cannot be passing
        // merely because nothing in this setup is capable of making a request.
        //
        // Event-driven, not wall-clock-budgeted: await InitialDelayScheduled so the
        // fake clock is only advanced once the jitter timer is genuinely registered
        // (advancing sooner races the worker's own thread-pool scheduling), then
        // await RequestReceived so the assertion runs only once the real request has
        // actually landed rather than after a fixed real-time window.
        var handler = new RecordingHandler();
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = NewWorker(
            handler,
            new ReviewSignalsOptions { Reviewers = [new ReviewerOptions { Name = "Gitar", BotLogin = "gitar-app" }] },
            timeProvider
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await timeProvider.InitialDelayScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await handler.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _ = handler.Calls.Should().BeGreaterThan(0);

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }
}

// CB (fix round 1, M-1): covers the wiring inside CollectAsync that the Task
// 2-4 unit tests only exercise separately — the null-vs-zero alerts
// conversion, the needsAlerts gate, and the excluded-authors skip — by driving
// the real ReviewSignalEnrichmentWorker over a scripted HttpMessageHandler.
public class ReviewSignalEnrichmentWorkerCollectTests
{
    private const string RepoName = "repo-a";

    private sealed class RoutingHandler(string factsJson, HttpStatusCode alertsStatus, string alertsJson)
        : HttpMessageHandler
    {
        // The alerts endpoint is always the LAST network call CollectAsync makes for
        // every scenario this class drives (every test configures a CodeScanning
        // reviewer, so needsAlerts is always true) — it is therefore the real
        // completion signal a test can await, rather than a fixed real-time budget.
        public TaskCompletionSource AlertsRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var isAlertsRequest = path.Contains("code-scanning/alerts", StringComparison.Ordinal);
            var (status, body) = path switch
            {
                _ when path.EndsWith("/repos", StringComparison.Ordinal) => (
                    HttpStatusCode.OK,
                    $$"""[{"name":"{{RepoName}}","html_url":"https://github.com/FixPortal/{{RepoName}}","private":false,"archived":false,"default_branch":"main"}]"""
                ),
                "/graphql" => (HttpStatusCode.OK, factsJson),
                _ when isAlertsRequest => (alertsStatus, alertsJson),
                _ => (HttpStatusCode.OK, "[]"),
            };
            if (isAlertsRequest)
            {
                AlertsRequestReceived.TrySetResult();
            }
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }
    }

    // Plain placeholder substitution rather than raw-string interpolation: the JSON's
    // own run of closing braces right after the contexts hole is ambiguous for the
    // $$"""...""" interpolation-brace-counting rule (CS9007).
    private static string FactsJson(string author, bool includeSuccessfulCodeScanningCheck)
    {
        var contexts = includeSuccessfulCodeScanningCheck
            ? """[{"name":"CodeQL","conclusion":"SUCCESS","checkSuite":{"app":{"slug":"github-code-scanning"}}}]"""
            : "[]";
        const string template = """
            {"data":{"repository":{"pullRequests":{"nodes":[
              {"number":181,"author":{"login":"__AUTHOR__"},
               "labels":{"nodes":[]},"reviews":{"nodes":[]},"reviewThreads":{"nodes":[]},
               "commits":{"nodes":[{"commit":{"oid":"sha","statusCheckRollup":{"contexts":{"nodes":__CONTEXTS__}}}}]}}
            ]}}}}
            """;
        return template.Replace("__AUTHOR__", author).Replace("__CONTEXTS__", contexts);
    }

    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>> RunOneSweepAsync(
        RoutingHandler handler,
        ReviewSignalsOptions options
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var cache = new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>();
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            cache,
            Options.Create(options),
            gitHubOptions,
            timeProvider,
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        // Event-driven, not wall-clock-budgeted: wait for the jitter timer to be
        // genuinely registered before advancing past it (see the matching comment on
        // ReviewSignalWorkerGatingTests.Issues_requests_once_enabled_and_past_the_initial_jitter),
        // then wait for the alerts call — CollectAsync's last network request — to
        // land before touching the cache at all.
        await timeProvider.InitialDelayScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await handler.AlertsRequestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        // The alerts response landing does not itself prove RunSweepAsync's
        // cache.Update has run yet — a few more continuations (JSON parsing,
        // ReviewSignalFactory.Build, the sweep's own cache write) still need a
        // thread-pool turn. This is the "poll-until-condition helper with a generous
        // timeout" the house rule explicitly allows as the final settle, now gated
        // behind a real completion signal rather than being the only wait in the test.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!cache.TryGet(RepoName, out _) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await worker.StopAsync(TestContext.Current.CancellationToken);

        _ = cache
            .TryGet(RepoName, out var result)
            .Should()
            .BeTrue("the cold-start sweep should have written to the cache within 30s of the alerts response");
        return result!;
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "[]", false, ReviewSignalState.Pending, null)]
    [InlineData(HttpStatusCode.OK, "[]", true, ReviewSignalState.Clean, null)]
    [InlineData(
        HttpStatusCode.OK,
        """[{"most_recent_instance":{"ref":"refs/pull/181/head"}},{"most_recent_instance":{"ref":"refs/pull/181/head"}}]""",
        false,
        ReviewSignalState.Outstanding,
        2
    )]
    public async Task CollectAsync_converts_the_alerts_endpoint_response_into_the_right_signal(
        HttpStatusCode alertsStatus,
        string alertsJson,
        bool includeSuccessfulCheck,
        ReviewSignalState expectedState,
        int? expectedCount
    )
    {
        var handler = new RoutingHandler(FactsJson("chris", includeSuccessfulCheck), alertsStatus, alertsJson);
        var options = new ReviewSignalsOptions
        {
            Reviewers = [new ReviewerOptions { Name = "CodeQL", Source = ReviewerSource.CodeScanning }],
        };

        var signals = await RunOneSweepAsync(handler, options);

        var signal = signals[181].Single(s => s.Name == "CodeQL");
        _ = signal.State.Should().Be(expectedState);
        _ = signal.Count.Should().Be(expectedCount);
    }

    // Both spellings, because the wire shape is not the one the config was first written
    // for: GraphQL reports a Bot node's login as "dependabot", WITHOUT the "[bot]"
    // suffix — that suffix is a REST-ism. A suffix-only exclusion list therefore matches
    // nothing here and every dependency PR grows a row of pills, against standing policy.
    [Theory]
    [InlineData("dependabot")]
    [InlineData("dependabot[bot]")]
    [InlineData("renovate")]
    public async Task An_excluded_authors_pull_request_carries_no_signals_at_all(string author)
    {
        var handler = new RoutingHandler(
            FactsJson(author, includeSuccessfulCodeScanningCheck: true),
            HttpStatusCode.OK,
            "[]"
        );
        var options = new ReviewSignalsOptions
        {
            Reviewers = [new ReviewerOptions { Name = "CodeQL", Source = ReviewerSource.CodeScanning }],
            ExcludedAuthors = ["dependabot", "dependabot[bot]", "renovate", "renovate[bot]"],
        };

        var signals = await RunOneSweepAsync(handler, options);

        _ = signals.ContainsKey(181).Should().BeFalse();
    }
}
