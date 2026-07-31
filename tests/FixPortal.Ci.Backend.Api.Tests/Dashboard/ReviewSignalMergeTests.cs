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
        new(number, $"PR {number}", "chris", $"https://github.com/FixPortal/repo/pull/{number}", false, Instant.FromUnixTimeSeconds(1));

    private static readonly IReadOnlyList<ReviewSignal> Signals =
    [
        new("Gitar", ReviewSignalState.Clean, null, null),
    ];

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
        var cache = new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(clock, Duration.FromMinutes(10));
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
    // incremented with Interlocked because the positive-control test below races
    // the fake-time-driven sweep against the assertion thread.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/repos", StringComparison.Ordinal)
                ? """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]"""
                : "[]";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
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
        // right reason instead of vacuously passing on an un-awaited StartAsync.
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

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
        var handler = new RecordingHandler();
        var timeProvider = new FakeTimeProvider();
        var worker = NewWorker(
            handler,
            new ReviewSignalsOptions { Reviewers = [new ReviewerOptions { Name = "Gitar", BotLogin = "gitar-app" }] },
            timeProvider
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // The fake-time jitter timer's registration can lag the call to StartAsync by
        // a beat of real scheduling, so a single Advance can race it. Step the clock
        // repeatedly (well past the 15s jitter ceiling) with a real yield between
        // steps, rather than betting on one Advance landing after the timer exists.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (handler.Calls == 0 && DateTime.UtcNow < deadline)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

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

    private sealed class RoutingHandler(string factsJson, HttpStatusCode alertsStatus, string alertsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (status, body) = path switch
            {
                _ when path.EndsWith("/repos", StringComparison.Ordinal) => (
                    HttpStatusCode.OK,
                    $$"""[{"name":"{{RepoName}}","html_url":"https://github.com/FixPortal/{{RepoName}}","private":false,"archived":false,"default_branch":"main"}]"""
                ),
                "/graphql" => (HttpStatusCode.OK, factsJson),
                _ when path.Contains("code-scanning/alerts", StringComparison.Ordinal) => (alertsStatus, alertsJson),
                _ => (HttpStatusCode.OK, "[]"),
            };
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
        HttpMessageHandler handler,
        ReviewSignalsOptions options
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var cache = new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>();
        var timeProvider = new FakeTimeProvider();
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

        // See the matching comment in ReviewSignalWorkerGatingTests: step the fake
        // clock repeatedly with a real yield between steps rather than betting on one
        // Advance landing after the jitter timer is actually registered.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!cache.TryGet(RepoName, out _) && DateTime.UtcNow < deadline)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await worker.StopAsync(TestContext.Current.CancellationToken);

        _ = cache.TryGet(RepoName, out var result).Should().BeTrue("the cold-start sweep should have populated the cache");
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

    [Fact]
    public async Task An_excluded_authors_pull_request_carries_no_signals_at_all()
    {
        var handler = new RoutingHandler(FactsJson("dependabot[bot]", includeSuccessfulCodeScanningCheck: true), HttpStatusCode.OK, "[]");
        var options = new ReviewSignalsOptions
        {
            Reviewers = [new ReviewerOptions { Name = "CodeQL", Source = ReviewerSource.CodeScanning }],
            ExcludedAuthors = ["dependabot[bot]"],
        };

        var signals = await RunOneSweepAsync(handler, options);

        _ = signals.ContainsKey(181).Should().BeFalse();
    }
}
