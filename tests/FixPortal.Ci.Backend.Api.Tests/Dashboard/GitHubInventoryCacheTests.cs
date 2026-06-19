using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public sealed class GitHubInventoryCacheTests : IDisposable
{
    private const int TtlSeconds = 60;

    // Each Build() registers its HttpClient here; xUnit makes a fresh test-class
    // instance per test, so Dispose disposes that test's clients (and, since
    // HttpClient owns its handler, the CountingHandler with them).
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    // Counts requests per inventory endpoint so a test can assert the cache
    // collapsed N callers into one GitHub fetch. Returns canned snake_case JSON
    // matching the shapes ListRepositoriesAsync / ListWorkflowsAsync expect.
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RepoCalls;
        private readonly Dictionary<string, int> _workflowCalls = new(StringComparer.OrdinalIgnoreCase);

        public int WorkflowCalls(string repo)
        {
            lock (_workflowCalls)
            {
                return _workflowCalls.TryGetValue(repo, out var n) ? n : 0;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken); // widen the window so the single-flight test really overlaps
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/repos", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref RepoCalls);
                json = """[{"name":"a","html_url":"https://github.com/FixPortal/a","private":false,"archived":false,"default_branch":"main"}]""";
            }
            else if (path.Contains("/actions/workflows", StringComparison.Ordinal))
            {
                // .../repos/FixPortal/{repo}/actions/workflows
                var repo = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^3];
                lock (_workflowCalls)
                {
                    _workflowCalls[repo] = WorkflowCalls(repo) + 1;
                }

                json = """{"workflows":[{"id":1,"name":"CI","path":".github/workflows/ci.yml","state":"active"}]}""";
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class MutableClock(Instant start) : IClock
    {
        public Instant Now { get; set; } = start;
        public Instant GetCurrentInstant() => Now;
    }

    private (GitHubInventoryCache Cache, CountingHandler Handler, MutableClock Clock) Build()
    {
        var handler = new CountingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        _clients.Add(http);
        var gitHub = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboard = Options.Create(new DashboardOptions { RefreshSeconds = TtlSeconds, SnapshotPath = "snapshot.json" });
        var client = new GitHubOrgClient(http, gitHub, dashboard, new GitHubETagStore());
        var clock = new MutableClock(Instant.FromUtc(2026, 5, 31, 6, 0));
        return (new GitHubInventoryCache(client, clock, dashboard), handler, clock);
    }

    [Fact]
    public async Task GetRepositories_fetches_once_and_serves_the_cache_within_ttl()
    {
        var (cache, handler, _) = Build();

        var first = await cache.GetRepositoriesAsync(CancellationToken.None);
        _ = await cache.GetRepositoriesAsync(CancellationToken.None);
        _ = await cache.GetRepositoriesAsync(CancellationToken.None);

        _ = handler.RepoCalls.Should().Be(1);
        _ = first.Should().ContainSingle(r => r.Name == "a");
    }

    [Fact]
    public async Task GetRepositories_refetches_after_the_ttl_expires()
    {
        var (cache, handler, clock) = Build();

        _ = await cache.GetRepositoriesAsync(CancellationToken.None);
        clock.Now += Duration.FromSeconds(TtlSeconds + 1);
        _ = await cache.GetRepositoriesAsync(CancellationToken.None);

        _ = handler.RepoCalls.Should().Be(2);
    }

    [Fact]
    public async Task GetWorkflows_caches_per_repo_independently()
    {
        var (cache, handler, _) = Build();

        _ = await cache.GetWorkflowsAsync("a", CancellationToken.None);
        _ = await cache.GetWorkflowsAsync("a", CancellationToken.None);
        _ = await cache.GetWorkflowsAsync("b", CancellationToken.None);

        _ = handler.WorkflowCalls("a").Should().Be(1); // second call to "a" served from cache
        _ = handler.WorkflowCalls("b").Should().Be(1); // a different repo is fetched on its own
    }

    [Fact]
    public async Task Concurrent_callers_collapse_into_a_single_fetch()
    {
        var (cache, handler, _) = Build();

        _ = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetRepositoriesAsync(CancellationToken.None)));

        _ = handler.RepoCalls.Should().Be(1); // single-flight: 20 concurrent callers, one GitHub call
    }
}
