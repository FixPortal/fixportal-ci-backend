using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

// MergeStateEnrichmentWorker.CollectAsync was never constructed by a test. These drive the
// real worker loop over a scripted handler. "Converged" (steady-state timer registered,
// not the 5-minute retry delay) is the observable that separates a soft-failed sweep
// (CollectAsync returned null) from a thrown one.
public class MergeStateEnrichmentWorkerTests
{
    private const string OpenPullsJson =
        """[{"number":1,"title":"t","user":{"login":"u"},"html_url":"https://x/1","draft":false,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","head":{"sha":"abc"}}]""";

    private const string MergeStateJson = """
        {"data":{
          "repository":{"pr1":{"number":1,"isDraft":false,"mergeable":"MERGEABLE","mergeStateStatus":"CLEAN","headRefOid":"abc"}},
          "rateLimit":{"cost":1,"remaining":10,"resetAt":"2030-01-01T00:00:00Z"}
        }}
        """;

    private sealed class ScriptedHandler(
        string[] repos,
        Func<string, string> pullsFor,
        Func<HttpRequestMessage, HttpResponseMessage> graphQl
    ) : HttpMessageHandler
    {
        public List<string> GraphQlCalls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/orgs/FixPortal/repos")
            {
                var json =
                    "["
                    + string.Join(
                        ",",
                        repos.Select(r =>
                            $$"""{"name":"{{r}}","html_url":"https://github.com/FixPortal/{{r}}","private":false,"archived":false,"default_branch":"main"}"""
                        )
                    )
                    + "]";
                return Task.FromResult(Json(json));
            }
            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(pullsFor(path.Split('/')[3])));
            }
            if (path == "/graphql")
            {
                lock (GraphQlCalls)
                {
                    GraphQlCalls.Add("graphql");
                }
                return Task.FromResult(graphQl(request));
            }
            throw new InvalidOperationException($"Unexpected GitHub request: {request.RequestUri}");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage GraphQlOk(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MergeStateEnrichmentWorker NewWorker(
        HttpClient http,
        PerRepoCache<IReadOnlyDictionary<int, PrMergeState>> cache,
        TrackingFakeTimeProvider timeProvider
    )
    {
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        return new MergeStateEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, clock, dashboardOptions),
            cache,
            Options.Create(new MergeStateOptions { RefreshSeconds = 60 }),
            Options.Create(new ReviewSignalsOptions()),
            timeProvider,
            clock,
            NullLogger<MergeStateEnrichmentWorker>.Instance
        );
    }

    // Runs the cold-start sweep and reports whether it converged to steady state.
    private static async Task<bool> RunColdStartAsync(MergeStateEnrichmentWorker worker, TrackingFakeTimeProvider time)
    {
        var ct = TestContext.Current.CancellationToken;
        await worker.StartAsync(ct);
        try
        {
            await time.InitialDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            time.Advance(TimeSpan.FromSeconds(15));
            var steady = time.SteadyStateTimerScheduled.Task;
            var done = await Task.WhenAny(steady, time.RetryDelayScheduled.Task)
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
            return done == steady;
        }
        finally
        {
            await worker.StopAsync(ct);
        }
    }

    private static IReadOnlyDictionary<int, PrMergeState> Seeded() =>
        new Dictionary<int, PrMergeState> { [1] = new(1, false, "MERGEABLE", "CLEAN", "old") };

    [Fact]
    public async Task A_repository_with_no_open_pull_requests_replaces_the_cache_with_an_empty_dictionary()
    {
        using var handler = new ScriptedHandler(["repo-a"], _ => "[]", _ => throw new InvalidOperationException());
        var cache = new PerRepoCache<IReadOnlyDictionary<int, PrMergeState>>();
        cache.Update("repo-a", Seeded());
        var time = new TrackingFakeTimeProvider();
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        using var worker = NewWorker(http, cache, time);

        var converged = await RunColdStartAsync(worker, time);

        _ = converged.Should().BeTrue();
        _ = cache.TryGet("repo-a", out var cached).Should().BeTrue();
        _ = cached.Should().BeEmpty("null would strand the merge state of a pull request that just merged");
    }

    [Fact]
    public async Task A_sweep_stopped_by_the_reserve_floor_keeps_last_known_good_for_the_unreached_repository()
    {
        // repo-a's response records remaining=10 (below the 1000-point default reserve, reset
        // far in the future); the guard then stops repo-b before it spends a query.
        using var handler = new ScriptedHandler(
            ["repo-a", "repo-b"],
            _ => OpenPullsJson,
            _ => GraphQlOk(MergeStateJson)
        );
        var cache = new PerRepoCache<IReadOnlyDictionary<int, PrMergeState>>();
        var previous = Seeded();
        cache.Update("repo-b", previous);
        var time = new TrackingFakeTimeProvider();
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        using var worker = NewWorker(http, cache, time);

        var converged = await RunColdStartAsync(worker, time);

        _ = converged.Should().BeTrue("a reserve stop is a soft null, not a failed sweep");
        _ = handler.GraphQlCalls.Should().HaveCount(1);
        _ = cache.TryGet("repo-a", out var a).Should().BeTrue();
        _ = a![1].HeadSha.Should().Be("abc");
        _ = cache.TryGet("repo-b", out var b).Should().BeTrue();
        _ = b.Should().BeSameAs(previous);
    }

    public enum Failure
    {
        RateLimited403,
        Unauthorized401,
        ServerError500,
        TimeoutWithoutCancellation,
    }

    [Theory]
    [InlineData(Failure.RateLimited403)]
    [InlineData(Failure.Unauthorized401)]
    [InlineData(Failure.ServerError500)]
    [InlineData(Failure.TimeoutWithoutCancellation)]
    public async Task A_transport_rate_limit_auth_or_timeout_failure_soft_fails_and_keeps_last_known_good(
        Failure failure
    )
    {
        using var handler = new ScriptedHandler(
            ["repo-a"],
            _ => OpenPullsJson,
            req =>
            {
                switch (failure)
                {
                    case Failure.RateLimited403:
                        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        _ = limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                        return limited;
                    case Failure.Unauthorized401:
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    case Failure.ServerError500:
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    default:
                        throw new TaskCanceledException("GitHub timed out");
                }
            }
        );
        var cache = new PerRepoCache<IReadOnlyDictionary<int, PrMergeState>>();
        var previous = Seeded();
        cache.Update("repo-a", previous);
        var time = new TrackingFakeTimeProvider();
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        using var worker = NewWorker(http, cache, time);

        var converged = await RunColdStartAsync(worker, time);

        _ = converged.Should().BeTrue("a soft-failed sweep must not drive the 5-minute cold-start retry");
        _ = cache.TryGet("repo-a", out var cached).Should().BeTrue();
        _ = cached.Should().BeSameAs(previous);
    }
}
