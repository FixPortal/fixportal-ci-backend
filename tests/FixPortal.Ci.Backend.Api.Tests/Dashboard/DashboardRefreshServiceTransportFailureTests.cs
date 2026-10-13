using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

/// <summary>
/// E4 (test audit 2026-10-11): <c>CollectRepoAsync</c> degrades ONE repo on a transport fault, a
/// malformed body or an HTTP timeout, and lets host shutdown propagate. The filter has four
/// clauses and only the rate-limit cascade (which reaches the cancellation clause incidentally as a
/// sibling cancel) exercised any of them, so deleting the transport, JSON or timeout clause left
/// the suite green and one flaky repo then failed the whole refresh.
///
/// A real timeout is a <see cref="TaskCanceledException"/> while neither the host token nor the
/// rate-limit token is cancelled; shutdown is the HOST token itself being cancelled. The malformed
/// body comes from a per-repo endpoint (the runs listing), not the org repo list, which would fail
/// the inventory call instead.
/// </summary>
public class DashboardRefreshServiceTransportFailureTests
{
    public enum Fault
    {
        Timeout,
        TransportError,
        MalformedRunsBody,
    }

    [Theory]
    [InlineData(Fault.Timeout)]
    [InlineData(Fault.TransportError)]
    [InlineData(Fault.MalformedRunsBody)]
    public async Task One_repos_transport_fault_degrades_only_that_repo(Fault fault)
    {
        var (sut, state, store, http) = Build(new FaultingRepoHandler(fault, hostCancel: null));
        using var owned = http;

        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        await sut.RefreshAsync(ceiling.Token);

        _ = state.Current.Should().NotBeNull("the refresh must complete and publish the healthy repo");
        _ = state.Current!.Repositories.Select(r => r.Name).Should().Contain("healthy");
        await store.DidNotReceive().SaveAsync(Arg.Any<DashboardSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Host_shutdown_during_a_repo_fetch_propagates_instead_of_being_absorbed()
    {
        using var host = new CancellationTokenSource();
        var (sut, _, _, http) = Build(new FaultingRepoHandler(Fault.Timeout, host));
        using var owned = http;

        var act = () => sut.RefreshAsync(host.Token);

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static (
        DashboardRefreshService Sut,
        DashboardSnapshotState State,
        IDashboardSnapshotStore Store,
        HttpClient Http
    ) Build(HttpMessageHandler handler)
    {
        var state = new DashboardSnapshotState();
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
        var gitHubOptions = Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" });
        var dashboardOptions = Options.Create(new DashboardOptions { SnapshotPath = "s.json", RefreshSeconds = 60 });
        var store = Substitute.For<IDashboardSnapshotStore>();
        var clock = new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0));
        var client = new GitHubOrgClient(http, gitHubOptions, dashboardOptions, new GitHubETagStore());
        var sut = new DashboardRefreshService(
            client,
            new GitHubInventoryCache(client, clock, dashboardOptions),
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
            NullLogger<DashboardRefreshService>.Instance
        );
        return (sut, state, store, http);
    }

    // Two repos: "flaky" misbehaves according to the fault, "healthy" answers normally.
    private sealed class FaultingRepoHandler(Fault fault, CancellationTokenSource? hostCancel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var flaky = path.Contains("/flaky/", StringComparison.Ordinal);

            if (path.Contains("/orgs/", StringComparison.Ordinal) && path.EndsWith("/repos", StringComparison.Ordinal))
            {
                // The shutdown case has exactly one repo, so nothing else in the batch can
                // observe the cancelled host token and mask whether the filter let it through.
                if (hostCancel is not null)
                {
                    return Task.FromResult(
                        Ok(
                            """[{"name":"flaky","html_url":"https://github.com/FixPortal/flaky","private":false,"archived":false,"default_branch":"main"}]"""
                        )
                    );
                }

                return Task.FromResult(
                    Ok(
                        """
                        [
                          {"name":"flaky","html_url":"https://github.com/FixPortal/flaky","private":false,"archived":false,"default_branch":"main"},
                          {"name":"healthy","html_url":"https://github.com/FixPortal/healthy","private":false,"archived":false,"default_branch":"main"}
                        ]
                        """
                    )
                );
            }

            if (path.EndsWith("/actions/workflows", StringComparison.Ordinal))
            {
                if (flaky && fault == Fault.Timeout)
                {
                    // Shutdown variant: the HOST token is cancelled, then the request observes it.
                    if (hostCancel is not null)
                    {
                        hostCancel.Cancel();
                        throw new TaskCanceledException("host shutdown", null, cancellationToken);
                    }

                    // Timeout variant: HttpClient surfaces a timeout as a TaskCanceledException
                    // while no token has been cancelled.
                    throw new TaskCanceledException("The request timed out.", new TimeoutException());
                }

                if (flaky && fault == Fault.TransportError)
                {
                    throw new HttpRequestException("Connection refused.");
                }

                return Task.FromResult(
                    Ok(
                        flaky
                            ? """{"workflows":[{"id":1,"name":"CI","path":".github/workflows/ci.yml","state":"active"}]}"""
                            : """{"workflows":[]}"""
                    )
                );
            }

            if (
                path.Contains("/actions/workflows/", StringComparison.Ordinal)
                && path.EndsWith("/runs", StringComparison.Ordinal)
            )
            {
                return Task.FromResult(
                    Ok(flaky && fault == Fault.MalformedRunsBody ? "this is {not json" : """{"workflow_runs":[]}""")
                );
            }

            return Task.FromResult(Ok("[]"));
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
