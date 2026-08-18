using System.Diagnostics;
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
    private const string HeadA = "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3";
    private const string HeadB = "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12";

    private static PullRequest Pr(int number, string? headSha = HeadA) =>
        new(
            number,
            $"PR {number}",
            "chris",
            $"https://github.com/FixPortal/repo/pull/{number}",
            false,
            Instant.FromUnixTimeSeconds(1),
            HeadSha: headSha
        );

    private static readonly IReadOnlyList<ReviewSignal> Signals = [new("Gitar", ReviewSignalState.Clean, null, null)];

    private static CachedReviewSignals Cached(string? headSha = HeadA, IReadOnlyList<ReviewSignal>? signals = null) =>
        new(headSha, signals ?? Signals);

    [Fact]
    public void Attaches_signals_to_the_matching_pull_request_only()
    {
        var merged = DashboardRefreshService.ApplyReviewSignals(
            [Pr(181), Pr(179)],
            new Dictionary<int, CachedReviewSignals> { [181] = Cached() }
        );

        _ = merged[0].ReviewSignals.Should().BeEquivalentTo(Signals);
        _ = merged[1].ReviewSignals.Should().BeNull();
    }

    // H3: a cached signal is earned against one head. A pull request pushed since the
    // sweep must degrade to "no signals" (and so an unknown verdict) rather than
    // republishing the previous head's Clean until the worker's next sweep.
    [Fact]
    public void Signals_computed_against_an_older_head_are_not_attached_after_a_push()
    {
        var merged = DashboardRefreshService.ApplyReviewSignals(
            [Pr(181, HeadB)],
            new Dictionary<int, CachedReviewSignals> { [181] = Cached(HeadA) }
        );

        _ = merged[0]
            .ReviewSignals.Should()
            .BeNull("the cached signals were earned against a head this PR no longer has");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Signals_without_a_recorded_head_never_attach(string? cachedHead)
    {
        var merged = DashboardRefreshService.ApplyReviewSignals(
            [Pr(181)],
            new Dictionary<int, CachedReviewSignals> { [181] = Cached(cachedHead) }
        );

        _ = merged[0].ReviewSignals.Should().BeNull("a signal that cannot name its head cannot prove currency");
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
            new Dictionary<int, CachedReviewSignals> { [181] = Cached() }
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
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(clock, Duration.FromMinutes(10));
        cache.Update("repo", new Dictionary<int, CachedReviewSignals> { [181] = Cached() });

        clock.AdvanceMinutes(11);

        _ = cache.TryGet("repo", out var stale).Should().BeFalse();
        _ = stale.Should().BeNull();

        var merged = DashboardRefreshService.ApplyReviewSignals([Pr(181)], stale);
        _ = merged[0].ReviewSignals.Should().BeNull();
    }
}

// The GraphQL points budget is metered per USER, not per token, so this worker shares
// its 5,000/hour with any human running gh. Before this guard it read `remaining` off
// every response, logged it, and swept again regardless — spending to zero and blocking
// the CLI with an error naming a numeric user ID. The reserve is the load-bearing fix:
// the cadence and query fan-out are tuned to today's repo count and go over again as the
// estate grows, but the floor holds whatever they drift to.
public class ReviewSignalReserveTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 2, 17, 10, 0);
    private static readonly string FutureReset = "2026-08-02T17:38:33Z";
    private static readonly string PastReset = "2026-08-02T16:38:13Z";

    [Theory]
    // Healthy budget sweeps; at or above the floor is not below it.
    [InlineData(4000, 1000, false)]
    [InlineData(1000, 1000, false)]
    // Below the floor with the window still open: hold the reserve.
    [InlineData(999, 1000, true)]
    [InlineData(16, 1000, true)]
    // Reserve disabled restores the original drain-to-empty behaviour.
    [InlineData(0, 0, false)]
    public void The_reserve_holds_only_while_the_budget_is_genuinely_low(int remaining, int reserve, bool expected)
    {
        var budget = new GraphQlRateLimit(Cost: 53, Remaining: remaining, ResetAt: FutureReset);

        _ = ReviewSignalEnrichmentWorker.IsBelowReserve(budget, reserve, Now).Should().Be(expected);
    }

    [Fact]
    public void A_budget_observed_before_its_reset_does_not_latch_the_guard_shut()
    {
        // The failure this exists to prevent: once the guard trips the worker stops
        // querying, so Remaining is frozen at the value that tripped it and cannot
        // improve on its own. Expiring the observation at ResetAt is the only thing that
        // lets the next sweep through — without it the guard latches until the process
        // restarts, trading a starved CLI for a permanently blank board.
        var exhausted = new GraphQlRateLimit(Cost: 53, Remaining: 0, ResetAt: PastReset);

        _ = ReviewSignalEnrichmentWorker.IsBelowReserve(exhausted, reserve: 1000, Now).Should().BeFalse();
    }

    [Fact]
    public void Cold_start_sweeps_because_no_budget_has_been_observed_yet()
    {
        _ = ReviewSignalEnrichmentWorker.IsBelowReserve(null, reserve: 1000, Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    public void An_unreadable_reset_fails_open_rather_than_wedging_the_worker(string? resetAt)
    {
        // Fail open deliberately: a guard that cannot prove the budget is still low must
        // not be the thing that permanently blanks the board.
        var budget = new GraphQlRateLimit(Cost: 53, Remaining: 0, ResetAt: resetAt);

        _ = ReviewSignalEnrichmentWorker.IsBelowReserve(budget, reserve: 1000, Now).Should().BeFalse();
    }
}

public sealed class ReviewSignalWorkerGatingTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

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

    private ReviewSignalEnrichmentWorker NewWorker(
        RecordingHandler handler,
        ReviewSignalsOptions options,
        TimeProvider timeProvider
    )
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        _httpClients.Add(http);
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        return new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(),
            Options.Create(options),
            gitHubOptions,
            timeProvider,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
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

    // The REST open-PR payload the watermark is built from. head.sha and updated_at are
    // the two fields the diff turns on; the rest is there so the DTO binds as it does in
    // production. PR 181 is unknown on the first sweep, so it is dirty and gets fetched.
    private const string OpenPullsJson = """
        [{"number":181,"title":"PR 181","user":{"login":"chris"},
          "html_url":"https://github.com/FixPortal/repo-a/pull/181","draft":false,
          "created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z",
          "head":{"sha":"sha"}}]
        """;

    private sealed class RoutingHandler(
        string factsJson,
        HttpStatusCode alertsStatus,
        string alertsJson,
        bool repoPrivate = false
    ) : HttpMessageHandler
    {
        private int _graphQlCalls;
        private int _alertsCalls;

        // Every path the scanning products live behind, so a test can assert the
        // visibility filter stopped the request rather than merely dropped the pill.
        public bool ScanningEndpointCalled { get; private set; }

        public int GraphQlCalls => _graphQlCalls;

        // The completion signal for a sweep that makes no alerts call at all: with a
        // PublicOnly reviewer filtered out on a private repo, AlertsRequestReceived never
        // fires, and waiting on it would fail the test for the very reason it passes.
        public TaskCompletionSource GraphQlRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The alerts endpoint is always the LAST network call CollectAsync makes for
        // every scenario this class drives (every test configures a CodeScanning
        // reviewer, so needsAlerts is always true) — it is therefore the real
        // completion signal a test can await, rather than a fixed real-time budget.
        public TaskCompletionSource AlertsRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fires on the SECOND alerts call — the completion signal for a second
        // recomputing sweep, which is what the cold-cache recovery test (H5) awaits.
        public TaskCompletionSource SecondAlertsRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var isAlertsRequest = path.Contains("code-scanning/alerts", StringComparison.Ordinal);
            if (isAlertsRequest || path.Contains("secret-scanning/alerts", StringComparison.Ordinal))
            {
                ScanningEndpointCalled = true;
            }
            if (path == "/graphql")
            {
                _ = Interlocked.Increment(ref _graphQlCalls);
                GraphQlRequestReceived.TrySetResult();
            }
            var (status, body) = path switch
            {
                _ when path.EndsWith("/repos", StringComparison.Ordinal) => (
                    HttpStatusCode.OK,
                    $$"""[{"name":"{{RepoName}}","html_url":"https://github.com/FixPortal/{{RepoName}}","private":{{(repoPrivate ? "true" : "false")}},"archived":false,"default_branch":"main"}]"""
                ),
                "/graphql" => (HttpStatusCode.OK, factsJson),
                _ when isAlertsRequest => (alertsStatus, alertsJson),
                // The open-PR listing is now the FIRST call of every collect: it carries
                // the watermark, and a pull request absent from it is never fetched. An
                // empty list here would mean "nothing dirty", which spends nothing and
                // reaches neither GraphQL nor the alerts endpoint.
                _ when path.EndsWith("/pulls", StringComparison.Ordinal) => (HttpStatusCode.OK, OpenPullsJson),
                _ => (HttpStatusCode.OK, "[]"),
            };
            if (isAlertsRequest)
            {
                if (Interlocked.Increment(ref _alertsCalls) == 2)
                {
                    SecondAlertsRequest.TrySetResult();
                }
                AlertsRequestReceived.TrySetResult();
            }
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }
    }

    // The repos list answers normally; every GraphQL call gets a bare 403 (no
    // rate-limit headers), which GuardResponse surfaces as GitHubAuthException —
    // a PAT missing a scope, not a throttled request.
    private sealed class GraphQlAuthFailHandler : HttpMessageHandler
    {
        public TaskCompletionSource GraphQlRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/repos", StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
            GraphQlRequested.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    // Plain placeholder substitution rather than raw-string interpolation: the JSON's
    // own run of closing braces right after the contexts hole is ambiguous for the
    // $$"""...""" interpolation-brace-counting rule (CS9007).
    private static string FactsJson(string author, bool includeSuccessfulCodeScanningCheck)
    {
        var contexts = includeSuccessfulCodeScanningCheck
            ? """[{"name":"CodeQL","conclusion":"SUCCESS","checkSuite":{"app":{"slug":"github-advanced-security"}}}]"""
            : "[]";
        // The exact-PR query aliases one pullRequest field per requested number, so the
        // response is an alias-keyed object rather than a pullRequests connection.
        const string template = """
            {"data":{"repository":{
              "pr181":{"number":181,"author":{"login":"__AUTHOR__"},
               "labels":{"nodes":[]},"reviews":{"nodes":[]},"reviewThreads":{"nodes":[]},
               "commits":{"nodes":[{"commit":{"oid":"sha","statusCheckRollup":{"contexts":{"nodes":__CONTEXTS__}}}}]}}
            }}}
            """;
        return template.Replace("__AUTHOR__", author).Replace("__CONTEXTS__", contexts);
    }

    private static async Task<IReadOnlyDictionary<int, CachedReviewSignals>> RunOneSweepAsync(
        RoutingHandler handler,
        ReviewSignalsOptions options,
        bool expectAlertsCall = true
    )
    {
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>();
        var timeProvider = new TrackingFakeTimeProvider();
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            cache,
            Options.Create(options),
            gitHubOptions,
            timeProvider,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
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
        var lastCall = expectAlertsCall ? handler.AlertsRequestReceived.Task : handler.GraphQlRequestReceived.Task;
        await lastCall.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The alerts response landing does not itself prove RunSweepAsync's
        // cache.Update has run yet — a few more continuations (JSON parsing,
        // ReviewSignalFactory.Build, the sweep's own cache write) still need a
        // thread-pool turn. This is the "poll-until-condition helper with a generous
        // timeout" the house rule explicitly allows as the final settle, now gated
        // behind a real completion signal rather than being the only wait in the test.
        // Stopwatch rather than a wall-clock read: a monotonic source for an elapsed-time
        // budget, so an NTP step cannot stretch or collapse the settle window.
        var settleTimer = Stopwatch.StartNew();
        while (!cache.TryGet(RepoName, out _) && settleTimer.Elapsed < TimeSpan.FromSeconds(30))
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

        var signal = signals[181].Signals.Single(s => s.Name == "CodeQL");
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

    // The bug this pair exists for: GitHub's scanning products are paid on private
    // repositories and were switched off org-wide, so their endpoints 403/404 there. That
    // reads as Pending, Pending is not Clean or Disabled, and ReadyToMergeCalculator
    // therefore returned false for every pull request in all 20 private repos -- the
    // "Ready to merge" pill vanished estate-wide with no code change to explain it.
    [Theory]
    [InlineData(ReviewerSource.CodeScanning)]
    [InlineData(ReviewerSource.SecretScanning)]
    public async Task A_public_only_reviewer_is_omitted_entirely_on_a_private_repository(ReviewerSource source)
    {
        var handler = new RoutingHandler(
            FactsJson("chris", includeSuccessfulCodeScanningCheck: true),
            HttpStatusCode.OK,
            "[]",
            repoPrivate: true
        );
        var options = new ReviewSignalsOptions
        {
            Reviewers =
            [
                new ReviewerOptions
                {
                    Name = "Scanner",
                    Source = source,
                    PublicOnly = true,
                },
                // A second, unscoped reviewer proves the filter is selective rather than a
                // blanket "private repos get no pills", and gives the sweep something to
                // write so the assertion is not vacuous.
                new ReviewerOptions
                {
                    Name = "Gitar",
                    BotLogin = "gitar-app",
                    CommentsCountAsParticipation = true,
                },
            ],
        };

        var signals = await RunOneSweepAsync(handler, options, expectAlertsCall: false);

        _ = signals[181].Signals.Select(s => s.Name).Should().Equal("Gitar");
        // Not just an absent pill: the filtered reviewer must cost no request either.
        _ = handler.ScanningEndpointCalled.Should().BeFalse();
    }

    [Fact]
    public async Task A_public_only_reviewer_still_reports_on_a_public_repository()
    {
        var handler = new RoutingHandler(
            FactsJson("chris", includeSuccessfulCodeScanningCheck: true),
            HttpStatusCode.OK,
            "[]"
        );
        var options = new ReviewSignalsOptions
        {
            Reviewers =
            [
                new ReviewerOptions
                {
                    Name = "CodeQL",
                    Source = ReviewerSource.CodeScanning,
                    PublicOnly = true,
                },
            ],
        };

        var signals = await RunOneSweepAsync(handler, options);

        _ = signals[181].Signals.Single(s => s.Name == "CodeQL").State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public async Task A_graphql_authorization_failure_soft_fails_and_cold_start_still_converges()
    {
        // Regression cover for the GitHubAuthException arm of CollectAsync's catch:
        // before it, a mis-scoped PAT escaped to RunSweepAsync's generic catch,
        // counted as a per-repo sweep failure, and cold start retried every 5 minutes
        // indefinitely instead of settling into the steady cadence.
        var handler = new GraphQlAuthFailHandler();
        var timeProvider = new TrackingFakeTimeProvider();
        // Property set after the using declaration, not in an object initializer: an
        // initializer that throws leaves the instance unassigned and therefore undisposed.
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>();
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            cache,
            Options.Create(
                new ReviewSignalsOptions
                {
                    Reviewers = [new ReviewerOptions { Name = "Gitar", BotLogin = "gitar-app" }],
                    // Long cadence so the steady-state timer can never tick within the
                    // test: the assertion is about WHICH timer is registered, not what
                    // fires from it.
                    RefreshSeconds = 3600,
                }
            ),
            gitHubOptions,
            timeProvider,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await timeProvider.InitialDelayScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await handler.GraphQlRequested.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Convergence, event-driven: ExecuteAsync only registers the steady-state
        // PeriodicTimer once RunColdStartAsync has returned. A counted per-repo
        // failure would instead register the 5-minute retry delay — and this await
        // would time out.
        await timeProvider.SteadyStateTimerScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        _ = timeProvider
            .RetryDelayScheduled.Task.IsCompleted.Should()
            .BeFalse("a soft-failed collect must not count as a per-repo sweep failure");
        _ = cache.IsEmpty.Should().BeTrue("a soft-failed collect keeps last-known-good — here, an empty cache");

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    // H5: the per-repo signal cache expires after 3x the refresh cadence, and a null
    // collect (the GraphQL reserve guard holding, or a sustained soft-fail) never
    // refreshes it. On the first sweep after recovery over a QUIET repo the state used to
    // be self-consistently empty: clean watermarks, an empty cache read, nothing due, and
    // an empty merge written back with a fresh timestamp — a permanent latch. A cache
    // miss over non-empty watermarks must instead be treated as cold state: every open
    // pull request diffs dirty and is refetched.
    [Fact]
    public async Task A_cache_miss_over_clean_watermarks_refetches_every_open_pull_request()
    {
        var handler = new RoutingHandler(FactsJson("chris", true), HttpStatusCode.OK, "[]");
        var timeProvider = new TrackingFakeTimeProvider();
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        // The expiry clock is separate from the sweep timer: the cache entry written by
        // sweep 1 must be OLD when sweep 2 runs, while the cadence still ticks.
        var cacheClock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(
            cacheClock,
            Duration.FromSeconds(180)
        );
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            cache,
            Options.Create(
                new ReviewSignalsOptions
                {
                    Reviewers = [new ReviewerOptions { Name = "CodeQL", Source = ReviewerSource.CodeScanning }],
                    RefreshSeconds = 60,
                }
            ),
            gitHubOptions,
            timeProvider,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );

        // Sweep 1 (cold start): populates the cache and commits the watermarks.
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await timeProvider.InitialDelayScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await handler.AlertsRequestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        await timeProvider.SteadyStateTimerScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        _ = handler.GraphQlCalls.Should().Be(1, "the cold-start sweep fetched the one dirty pull request");

        // The cache entry expires (the reserve guard or a soft-fail would produce the
        // same gap), then the next cadence tick runs over an UNCHANGED repository: same
        // watermarks, so the diff is clean and only the cold-state treatment can spend.
        cacheClock.Advance(Duration.FromSeconds(200));
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await handler.SecondAlertsRequest.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        _ = handler
            .GraphQlCalls.Should()
            .Be(2, "a cache miss over committed watermarks must refetch, not settle into the empty latch");

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    // Follow-up to the cold-cache refetch: the emptied basis forces the refetch, but
    // TrackHeadTransitions must still compare against the REAL previous watermarks, so
    // a head that never moved keeps its original first-seen stamp. Restamping every
    // head to sweep-2's now would drop comments that counted before the expiry and pin
    // a comment-only reviewer to Pending for the rest of the head's life — its
    // announcing comment predates the restamp and is never reposted for the same head.
    [Fact]
    public async Task A_cache_miss_refetch_keeps_the_head_first_seen_anchor_for_unmoved_heads()
    {
        const string factsJson = """
            {"data":{"repository":{
              "pr181":{"number":181,"author":{"login":"chris"},
               "labels":{"nodes":[]},"reviews":{"nodes":[]},"reviewThreads":{"nodes":[]},
               "comments":{"nodes":[{"author":{"login":"gitar-bot"},"createdAt":"2026-01-01T00:05:00Z"}]},
               "commits":{"nodes":[{"commit":{"oid":"sha","committedDate":"2026-01-01T00:00:00Z","statusCheckRollup":{"contexts":{"nodes":[]}}}}]}}
            }}}
            """;
        var handler = new RoutingHandler(factsJson, HttpStatusCode.OK, "[]");
        var timeProvider = new TrackingFakeTimeProvider();
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.github.com/");
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var cacheClock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(
            cacheClock,
            Duration.FromSeconds(180)
        );
        var workerClock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            cache,
            Options.Create(
                new ReviewSignalsOptions
                {
                    Reviewers =
                    [
                        new ReviewerOptions
                        {
                            Name = "Gitar",
                            BotLogin = "gitar-bot",
                            CommentsCountAsParticipation = true,
                        },
                        new ReviewerOptions { Name = "CodeQL", Source = ReviewerSource.CodeScanning },
                    ],
                    RefreshSeconds = 60,
                }
            ),
            gitHubOptions,
            timeProvider,
            workerClock,
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );

        // Sweep 1 stamps the head at 00:00; the 00:05 comment counts and Gitar is Clean.
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await timeProvider.InitialDelayScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await handler.AlertsRequestReceived.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        await timeProvider.SteadyStateTimerScheduled.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        // The cache entry expires and the clock moves past the comment's instant, so a
        // restamped anchor (sweep-2 now = 00:10) would drop the 00:05 comment.
        cacheClock.Advance(Duration.FromSeconds(200));
        workerClock.Advance(Duration.FromMinutes(10));
        timeProvider.Advance(TimeSpan.FromSeconds(60));
        await handler.SecondAlertsRequest.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        _ = cache.TryGet(RepoName, out var signals).Should().BeTrue();
        var gitar = signals![181].Signals.Should().ContainSingle(s => s.Name == "Gitar").Subject;
        _ = gitar
            .State.Should()
            .Be(ReviewSignalState.Clean, "an unmoved head keeps its first-seen anchor across a cold-cache refetch");

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }
}
