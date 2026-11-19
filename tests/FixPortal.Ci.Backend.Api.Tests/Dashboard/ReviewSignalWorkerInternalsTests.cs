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

public sealed class ReviewSignalWorkerInternalsTests : IDisposable
{
    private const string Repo = "repo-a";
    private static readonly Instant T0 = Instant.FromUtc(2026, 1, 1, 0, 0);
    private static readonly IReadOnlyDictionary<int, PrWatermark> NoWatermarks = new Dictionary<int, PrWatermark>();
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.github.com/") };

    public void Dispose() => _http.Dispose();

    private static IReadOnlyDictionary<int, PrWatermark> Heads(string sha) =>
        new Dictionary<int, PrWatermark> { [1] = new(T0, sha) };

    private (ReviewSignalEnrichmentWorker Worker, FakeClock Clock) NewWorker()
    {
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 });
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var client = new GitHubOrgClient(_http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var clock = new FakeClock(T0);
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, clock, dashboardOptions),
            new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(),
            Options.Create(new ReviewSignalsOptions()),
            gitHubOptions,
            new FakeTimeProvider(),
            clock,
            NullLogger<ReviewSignalEnrichmentWorker>.Instance
        );
        return (worker, clock);
    }

    // The committed watermark is held back when a sweep fails, so the second sweep sees
    // head B with a watermark still naming A (or none). The decision must rest on the
    // seen entry the first sweep wrote.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failed_sweep_does_not_restamp_a_head_already_seen(bool watermarkStillNamesOldHead)
    {
        var (worker, clock) = NewWorker();
        var committed = watermarkStillNamesOldHead ? Heads("A") : NoWatermarks;

        _ = worker.TrackHeadTransitions(Repo, committed, Heads("A"));
        clock.Advance(Duration.FromSeconds(60));
        var firstB = worker.TrackHeadTransitions(Repo, committed, Heads("B"))[1];
        clock.Advance(Duration.FromSeconds(60));
        var secondB = worker.TrackHeadTransitions(Repo, committed, Heads("B"))[1];

        _ = firstB.FirstSeenAt.Should().Be(T0 + Duration.FromSeconds(60), "an observed A to B move is stamped now");
        _ = secondB.Should().Be(firstB, "the failed first sweep must not move the anchor");
    }

    // BuildOne turns a reviewer whose RequiredLabel is absent into Disabled; the
    // carry-over refresh of the repository-scoped secret count must not undo that.
    [Fact]
    public void A_refreshed_secret_count_leaves_a_carried_over_disabled_signal_disabled()
    {
        var (worker, _) = NewWorker();
        var reviewer = new ReviewerOptions
        {
            Name = "Secrets",
            Source = ReviewerSource.SecretScanning,
            RequiredLabel = "review-high",
        };
        var cached = new Dictionary<int, CachedReviewSignals>
        {
            [1] = new("A", [new ReviewSignal("Secrets", ReviewSignalState.Disabled, null, null)]),
        };

        var merged = worker.Merge(Repo, cached, Heads("A"), new Dictionary<int, PrReviewFacts>(), null, 3, [reviewer]);

        _ = merged[1].Signals.Should().ContainSingle().Which.State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void A_head_first_seen_with_no_watermark_carries_no_transition_instant()
    {
        var (worker, _) = NewWorker();

        var anchor = worker.TrackHeadTransitions(Repo, NoWatermarks, Heads("A"))[1];

        _ = anchor.FirstSeenAt.Should().Be(Instant.MinValue);
    }
}
