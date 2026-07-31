using System.Net;
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
}

public class ReviewSignalWorkerGatingTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    [Fact]
    public async Task Issues_no_requests_when_no_reviewers_are_configured()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            dashboardOptions,
            new GitHubETagStore()
        );
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)), dashboardOptions),
            new PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(),
            Options.Create(new ReviewSignalsOptions()),
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            new FakeTimeProvider(),
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        _ = handler.Calls.Should().Be(0);
    }
}
