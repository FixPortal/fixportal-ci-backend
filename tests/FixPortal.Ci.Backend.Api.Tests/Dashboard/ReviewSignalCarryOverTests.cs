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
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public partial class ReviewSignalEnrichmentWorkerCollectTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cached_unchanged_pull_request_uses_current_roster_and_repository_secret_count(bool removeReviewer)
    {
        var handler = new CarryOverHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var dashboard = Options.Create(new DashboardOptions { SnapshotPath = "unused", RefreshSeconds = 20 });
        var github = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "test-only" });
        var client = new GitHubOrgClient(http, github, dashboard, new GitHubETagStore());
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var cache = new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(clock, Duration.FromMinutes(10));
        List<ReviewerOptions> reviewers =
        [
            new() { Name = "CodeQL", Source = ReviewerSource.CodeScanning },
            new() { Name = "Secrets", Source = ReviewerSource.SecretScanning },
        ];
        var time = new TrackingFakeTimeProvider();
        var completed = new CompletedSweeps();
        using var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, clock, dashboard),
            cache,
            Options.Create(new ReviewSignalsOptions { Reviewers = reviewers, RefreshSeconds = 60 }),
            github,
            time,
            clock,
            completed
        );
        var ct = TestContext.Current.CancellationToken;
        await worker.StartAsync(ct);
        try
        {
            await time.InitialDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            time.Advance(TimeSpan.FromSeconds(15));
            await time.SteadyStateTimerScheduled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            cache.TryGet(RepoName, out var first).Should().BeTrue();
            first![181].Signals.Select(s => s.Name).Should().Equal("CodeQL", "Secrets");
            first[181].Signals.Single(s => s.Name == "Secrets").State.Should().Be(ReviewSignalState.Clean);

            if (removeReviewer)
            {
                reviewers.RemoveAt(0);
            }
            handler.SecondSweep = true;
            time.Advance(TimeSpan.FromSeconds(60));
            await completed.Second.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            handler.Queries.Should().HaveCount(2);
            handler
                .Queries[1]
                .Should()
                .Contain("182")
                .And.NotContain("181", "the unchanged PR must be carried, not fetched");
            handler.SecretCalls.Should().Be(2);
            cache.TryGet(RepoName, out var second).Should().BeTrue();
            second!.Keys.Should().BeEquivalentTo([181, 182]);
            second[181].HeadSha.Should().Be(first[181].HeadSha);
            IEnumerable<string> expectedRoster = removeReviewer ? ["Secrets"] : ["CodeQL", "Secrets"];
            second[181].Signals.Select(s => s.Name).Should().Equal(expectedRoster);
            foreach (var number in new[] { 181, 182 })
            {
                var secret = second[number].Signals.Single(s => s.Name == "Secrets");
                secret.State.Should().Be(ReviewSignalState.Outstanding);
                secret.Count.Should().Be(2, "repository-scoped refreshed counts apply to both carried and fetched PRs");
                secret.HtmlUrl.Should().Be("https://github.com/FixPortal/repo-a/security/secret-scanning");
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CompletedSweeps : ILogger<ReviewSignalEnrichmentWorker>
    {
        private int _count;
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (
                formatter(state, exception).StartsWith("PR review signals sweep issued", StringComparison.Ordinal)
                && Interlocked.Increment(ref _count) == 2
            )
            {
                Second.TrySetResult();
            }
        }
    }

    private sealed class CarryOverHandler : HttpMessageHandler
    {
        public bool SecondSweep { get; set; }
        public List<string> Queries { get; } = [];
        public int SecretCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path == "/graphql")
            {
                Queries.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                body = SecondSweep ? FactsJson("chris", true).Replace("181", "182") : FactsJson("chris", true);
            }
            else if (path.EndsWith("/repos", StringComparison.Ordinal))
            {
                body =
                    """[{"name":"repo-a","html_url":"https://github.com/FixPortal/repo-a","private":false,"archived":false,"default_branch":"main"}]""";
            }
            else if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                body = SecondSweep
                    ? OpenPullsJson.TrimEnd()[..^1] + "," + OpenPullsJson.Trim().TrimStart('[').Replace("181", "182")
                    : OpenPullsJson;
            }
            else if (path.Contains("secret-scanning/alerts", StringComparison.Ordinal))
            {
                SecretCalls++;
                body = SecondSweep ? """[{"number":1},{"number":2}]""" : "[]";
            }
            else
            {
                body = "[]";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
