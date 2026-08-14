using System.Collections.Concurrent;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

public sealed class DashboardRefreshService(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    IDashboardSnapshotStore store,
    DashboardSnapshotState state,
    PerRepoCache<RepoMetrics> metrics,
    [FromKeyedServices("deploys")] PerRepoCache<IReadOnlyList<JobSignal>> deploys,
    [FromKeyedServices("packages")] PerRepoCache<IReadOnlyList<JobSignal>> packages,
    PerRepoCache<MergedPullRequest> mergedPrs,
    PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>> reviewSignals,
    PerRepoCache<IReadOnlyDictionary<int, PrMergeState>> mergeStates,
    IOptions<ReviewSignalsOptions> reviewOptions,
    IOptions<GitHubOptions> gitHub,
    IClock clock,
    ILogger<DashboardRefreshService> logger
)
{
    private const int MaxParallelRepos = 6;

    // Snapshotted once rather than read per pull request: these are startup configuration,
    // and re-deriving the excluded-author set inside the per-repo loop would rebuild it
    // thousands of times a cycle for a value that cannot change.
    private readonly bool _reviewsConfigured = reviewOptions.Value is { Enabled: true, Reviewers.Count: > 0 };

    private readonly IReadOnlySet<string> _excludedAuthors = reviewOptions.Value.ExcludedAuthors.ToHashSet(
        StringComparer.OrdinalIgnoreCase
    );

    public async Task RefreshAsync(CancellationToken ct)
    {
        var repos = await inventory.GetRepositoriesAsync(ct);

        // When one repo hits the rate limit, cancel this CTS to stop the other parallel fetches
        // from burning the remaining quota. Linked to ct so host shutdown still propagates.
        using var rateLimitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(MaxParallelRepos);
        // Collect each repo's primary-endpoint auth failure and publish the health signal
        // ONCE at cycle end (worst-of-cycle). Publishing at the end — rather than clearing
        // up-front then re-detecting — means /api/health never momentarily reports Healthy
        // while a persistent auth failure is still in force. SendAsync no longer clears the
        // signal on success, so a healthy sibling cannot race-clear a failing repo's error
        // mid-cycle either. Single-writer: only the one DashboardRefreshWorker drives this.
        var authErrors = new ConcurrentQueue<string>();
        var tasks = new List<Task<(RepositorySnapshot Snapshot, bool FetchFailed, IReadOnlyList<WorkflowRun> Runs)>>(
            repos.Count
        );
        tasks.AddRange(repos.Select(repo => CollectRepoWithGateAsync(repo, gate, rateLimitCts, authErrors, ct)));
        var results = await Task.WhenAll(tasks);
        state.SetAuthError(authErrors.TryDequeue(out var authError) ? authError : null);

        var previous = state.Current;
        var repositories = MergeWithPrevious(results.Select(r => (r.Snapshot, r.FetchFailed)).ToList(), previous);
        repositories = InheritEnrichment(repositories, previous, mergedPrs);
        // Alphabetical by name; the public/private boards filter this list and a
        // filter preserves order, so one sort orders both groups ascending.
        repositories = repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var lastMerged = PickLatestMerged(repositories.Select(r => r.LastMergedPr));
        var now = clock.GetCurrentInstant();
        var ciTrend = BuildCiTrendForRefresh(results, now, previous);

        // Compute precise public snapshot (which separates public trend and public last merged PR)
        var publicRepos = repositories.Where(r => !r.Private).ToList();
        var publicLastMerged = PickLatestMerged(publicRepos.Select(r => r.LastMergedPr));
        var publicResults = results.Where(r => !r.Snapshot.Private).ToList();
        var publicCiTrend = BuildCiTrendForRefresh(publicResults, now, state.Public);
        var publicSnapshot = new DashboardSnapshot(
            now,
            gitHub.Value.Owner,
            publicRepos,
            BuildSummary(publicRepos),
            publicLastMerged,
            publicCiTrend
        );

        // Persist the public trend on the full snapshot so a cold-start restore
        // surfaces the accurate public trend rather than the lossy reclassification.
        var snapshot = new DashboardSnapshot(
            now,
            gitHub.Value.Owner,
            repositories,
            BuildSummary(repositories),
            lastMerged,
            ciTrend,
            PublicCiTrend: publicCiTrend
        );

        await PersistAndPublishAsync(
            store,
            state,
            snapshot,
            publicSnapshot,
            persist: ShouldPersist(results.Any(r => r.FetchFailed), previous is not null),
            logger,
            ct
        );
    }

    private async Task<(
        RepositorySnapshot Snapshot,
        bool FetchFailed,
        IReadOnlyList<WorkflowRun> Runs
    )> CollectRepoWithGateAsync(
        GitHubRepoDto repo,
        SemaphoreSlim gate,
        CancellationTokenSource rateLimitCts,
        ConcurrentQueue<string> authErrors,
        CancellationToken ct
    )
    {
        var rateLimitToken = rateLimitCts.Token;
        try
        {
            await gate.WaitAsync(rateLimitToken);
        }
        catch (OperationCanceledException) when (rateLimitToken.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Another repo hit the rate limit and cancelled rateLimitCts; skip this one.
            return (new RepositorySnapshot(repo.Name, repo.HtmlUrl, repo.Private, [], [], null, null, null), true, []);
        }
        try
        {
            return await CollectRepoAsync(repo, rateLimitCts, authErrors, rateLimitToken, ct);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<(
        RepositorySnapshot Snapshot,
        bool FetchFailed,
        IReadOnlyList<WorkflowRun> Runs
    )> CollectRepoAsync(
        GitHubRepoDto repo,
        CancellationTokenSource rateLimitCts,
        ConcurrentQueue<string> authErrors,
        CancellationToken rateLimitToken,
        CancellationToken ct
    )
    {
        try
        {
            var workflows = await inventory.GetWorkflowsAsync(repo.Name, rateLimitToken);
            var snaps = new List<WorkflowSnapshot>();
            var runs = new List<WorkflowRun>();
            foreach (var wf in workflows)
            {
                var wfRuns = await client.GetRecentRunsAsync(repo.Name, wf, rateLimitToken);
                runs.AddRange(wfRuns);
                var latest = wfRuns.Count > 0 ? wfRuns[0] : null;
                snaps.Add(
                    new WorkflowSnapshot(
                        wf.Name,
                        GitHubOrgClient.FileName(wf.Path),
                        GitHubOrgClient.ToSignalState(latest),
                        latest,
                        wfRuns
                    )
                );
            }
            // PRs are supplementary: listing them needs the "Pull requests: Read"
            // token scope, which workflow/run reads (Actions: Read) do not. Missing
            // pull-request access, or a transient endpoint error, must not degrade the repo.
            // only rate limits propagate and abort the batch.
            var openPrs = await TryListOpenPullRequestsAsync(repo.Name, rateLimitToken);
            _ = reviewSignals.TryGet(repo.Name, out var repoReviewSignals);
            _ = mergeStates.TryGet(repo.Name, out var repoMergeStates);
            var pullRequests = ApplyReadyToMerge(
                ApplyReviewSignals(openPrs, repoReviewSignals),
                repoMergeStates,
                _reviewsConfigured,
                _excludedAuthors
            );
            _ = metrics.TryGet(repo.Name, out var repoMetrics);
            _ = deploys.TryGet(repo.Name, out var repoDeploys);
            _ = packages.TryGet(repo.Name, out var repoPackages);
            return (
                new RepositorySnapshot(
                    repo.Name,
                    repo.HtmlUrl,
                    repo.Private,
                    snaps,
                    pullRequests,
                    repoMetrics,
                    repoDeploys,
                    repoPackages
                ),
                false,
                runs
            );
        }
        catch (GitHubRateLimitException ex)
        {
            // Cancel sibling fetches so they don't exhaust the remaining quota.
            logger.LogWarning(ex, "GitHub rate limit reached for {Repo}; aborting batch.", repo.Name);
            await rateLimitCts.CancelAsync();
            return (new RepositorySnapshot(repo.Name, repo.HtmlUrl, repo.Private, [], [], null, null, null), true, []);
        }
        // An auth/authz failure (401, or a non-rate-limit 403 — e.g. SSO not granted
        // or the PAT lacking Actions access on this one repo) is per-repo, NOT a
        // batch-wide condition like a rate limit. Degrade just this repo and let the
        // siblings complete; the error is recorded into authErrors and published once
        // at cycle end, so /api/health reliably reports Degraded without a transient
        // Healthy window. Without this catch the exception would escape Task.WhenAll
        // and abandon every sibling's freshly fetched signals.
        catch (GitHubAuthException ex)
        {
            logger.LogWarning(
                ex,
                "GitHub auth failed for {Repo}; preserving last-known-good for this repo only.",
                repo.Name
            );
            // Record for the cycle-end health signal (published once in RefreshAsync).
            authErrors.Enqueue(ex.Message);
            return (new RepositorySnapshot(repo.Name, repo.HtmlUrl, repo.Private, [], [], null, null, null), true, []);
        }
        // Treat transport/deserialization failures (and HTTP timeouts, which surface
        // as TaskCanceledException with the request token still un-cancelled) as a
        // degraded repo. Let genuine host-shutdown cancellation (ct triggered) propagate.
        catch (Exception ex)
            when (ex is HttpRequestException or JsonException
                || ex is OperationCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to collect signals for {Repo}; preserving last-known-good.", repo.Name);
            return (new RepositorySnapshot(repo.Name, repo.HtmlUrl, repo.Private, [], [], null, null, null), true, []);
        }
    }

    // Best-effort: pull requests are supplementary, so neither missing read access
    // nor a transient endpoint error may degrade the repo — show no pull requests and
    // keep the freshly fetched workflow/run signals. Authentication and authorization
    // responses are mapped to the domain auth exception; server and transport faults
    // use the standard HTTP exception. Both return an empty list. The domain rate-limit
    // exception deliberately propagates to abort the batch.
    private async Task<IReadOnlyList<PullRequest>> TryListOpenPullRequestsAsync(
        string repo,
        CancellationToken rateLimitToken
    )
    {
        try
        {
            return await client.ListOpenPullRequestsAsync(repo, rateLimitToken);
        }
        catch (GitHubAuthException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to list open PRs for {Repo} due to permissions; showing none (check the PAT's Pull requests: Read scope).",
                repo
            );
            return [];
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to list open PRs for {Repo} ({Status}); showing none for this cycle.",
                repo,
                ex.StatusCode
            );
            return [];
        }
    }

    /// <summary>
    /// Attaches cached review signals to the freshly-fetched pull requests. Signals are
    /// enriched on a slower cadence than the board refresh, so a PR with no cached
    /// entry keeps a null field rather than blocking the cycle.
    /// </summary>
    public static IReadOnlyList<PullRequest> ApplyReviewSignals(
        IReadOnlyList<PullRequest> prs,
        IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>? signals
    )
    {
        if (signals is null || signals.Count == 0 || prs.Count == 0)
        {
            return prs;
        }
        var merged = new List<PullRequest>(prs.Count);
        foreach (var pr in prs)
        {
            merged.Add(signals.TryGetValue(pr.Number, out var prSignals) ? pr with { ReviewSignals = prSignals } : pr);
        }
        return merged;
    }

    /// <summary>
    /// Stamps each pull request with the ready-to-merge verdict. Runs AFTER
    /// <see cref="ApplyReviewSignals"/>, because the verdict reads the signals that step
    /// attaches — reversing the order silently yields "unknown" for every pull request
    /// that actually had signals, which looks like the merge-state worker being broken.
    /// </summary>
    public static IReadOnlyList<PullRequest> ApplyReadyToMerge(
        IReadOnlyList<PullRequest> prs,
        IReadOnlyDictionary<int, PrMergeState>? mergeStates,
        bool reviewsConfigured,
        IReadOnlySet<string> excludedAuthors
    )
    {
        if (prs.Count == 0)
        {
            return prs;
        }

        var stamped = new List<PullRequest>(prs.Count);
        foreach (var pr in prs)
        {
            PrMergeState? state = null;
            _ = mergeStates?.TryGetValue(pr.Number, out state);
            stamped.Add(
                pr with
                {
                    ReadyToMerge = ReadyToMergeCalculator.Evaluate(pr, state, reviewsConfigured, excludedAuthors),
                }
            );
        }
        return stamped;
    }

    public static IReadOnlyList<RepositorySnapshot> MergeWithPrevious(
        IReadOnlyList<(RepositorySnapshot Snapshot, bool FetchFailed)> results,
        DashboardSnapshot? previous
    )
    {
        if (previous is null)
        {
            return results.Select(r => r.Snapshot).ToList();
        }

        var prior = previous.Repositories.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return results
            .Select(r =>
                r.FetchFailed && prior.TryGetValue(r.Snapshot.Name, out var p) ? WithoutReviewSignals(p) : r.Snapshot
            )
            .ToList();
    }

    /// <summary>
    /// Strips review signals from a prior snapshot before it is republished for a repo
    /// whose fetch failed. Everything else (workflows, runs, metrics) is genuinely
    /// last-known-good and is worth showing while a repo is rate-limited or degraded;
    /// a review signal is not. It is earned against one head commit, and a failing
    /// repo's head moves on while this substitution chains the same snapshot forward
    /// every cycle — which would keep a Clean pill alive indefinitely, the exact
    /// false pass the head-scoping and the cache TTL exist to prevent. Degrade to
    /// showing no pills instead.
    /// </summary>
    private static RepositorySnapshot WithoutReviewSignals(RepositorySnapshot prior) =>
        prior.PullRequests is null or { Count: 0 }
            ? prior
            : prior with
            {
                PullRequests =
                [
                    .. prior.PullRequests.Select(pr => pr with { ReviewSignals = null, ReadyToMerge = null }),
                ],
            };

    // On cold start the enrichment caches (metrics/deploys/packages) are empty until the
    // slow-cadence workers complete their first run (up to 300s). Without this, the first
    // refresh would publish null/empty enrichment and overwrite the restored snapshot's good
    // data. Inherit from the previous snapshot wherever the current value is missing.
    public static IReadOnlyList<RepositorySnapshot> InheritEnrichment(
        IReadOnlyList<RepositorySnapshot> current,
        DashboardSnapshot? previous,
        PerRepoCache<MergedPullRequest>? mergedPrs = null
    )
    {
        mergedPrs ??= new PerRepoCache<MergedPullRequest>();
        if (previous is null)
        {
            var hasAnyInCache = current.Any(r => mergedPrs.TryGet(r.Name, out _));
            if (!hasAnyInCache)
            {
                return current;
            }

            return current
                .Select(r =>
                {
                    _ = mergedPrs.TryGet(r.Name, out var m);
                    return r with { LastMergedPr = m };
                })
                .ToList();
        }

        var prior = previous.Repositories.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return current
            .Select(r =>
            {
                prior.TryGetValue(r.Name, out var p);

                _ = mergedPrs.TryGet(r.Name, out var m);
                var inheritedLastMerged = m ?? p?.LastMergedPr;

                var inheritedMetrics = r.Metrics ?? p?.Metrics;
                var inheritedDeploys = r.Deploys ?? p?.Deploys;
                var inheritedPackages = r.Packages ?? p?.Packages;

                return r with
                {
                    Metrics = inheritedMetrics,
                    Deploys = inheritedDeploys,
                    Packages = inheritedPackages,
                    LastMergedPr = inheritedLastMerged,
                };
            })
            .ToList();
    }

    public static bool ShouldPersist(bool anyFetchFailed, bool hasPrevious) => !anyFetchFailed || hasPrevious;

    public static MergedPullRequest? PickLatestMerged(IEnumerable<MergedPullRequest?> candidates) =>
        candidates.Where(m => m is not null).OrderByDescending(m => m!.MergedAt).FirstOrDefault();

    public static IReadOnlyList<CiTrendBucket> BuildCiTrendForRefresh(
        IReadOnlyList<(RepositorySnapshot Snapshot, bool FetchFailed, IReadOnlyList<WorkflowRun> Runs)> results,
        Instant now,
        DashboardSnapshot? previous
    )
    {
        var fresh = BuildCiTrend(results.SelectMany(r => r.Runs), now);
        if (previous?.CiTrend is null)
        {
            return fresh;
        }

        return MergeTrends(previous.CiTrend, fresh);
    }

    // Backfill a degraded refresh from the previous snapshot, aligning buckets by
    // their clock-hour start. Both trends are hour-anchored (see BuildCiTrend), so
    // two buckets with the same BucketStart are the same wall-clock hour — no
    // hour-shift arithmetic or rounding is needed, and the alignment cannot drift
    // when refreshes fall at different points within an hour. A fresh NoData bucket
    // inherits the previous state for the same hour — but only from a bucket that
    // was itself freshly observed, never an already-backfilled one. That bounds a
    // once-real (e.g. Failing) state to a single carry-over hop, so a sustained
    // outage cannot chain it forward indefinitely across repeated degraded refreshes.
    public static IReadOnlyList<CiTrendBucket> MergeTrends(
        IReadOnlyList<CiTrendBucket> previous,
        IReadOnlyList<CiTrendBucket> fresh
    )
    {
        var priorByHour = new Dictionary<Instant, CiTrendBucket>(previous.Count);
        foreach (var bucket in previous)
        {
            priorByHour[bucket.BucketStart] = bucket;
        }

        var result = new List<CiTrendBucket>(fresh.Count);
        foreach (var current in fresh)
        {
            if (current.State != CiTrendState.NoData)
            {
                result.Add(current);
                continue;
            }

            if (
                priorByHour.TryGetValue(current.BucketStart, out var prior)
                && prior.State != CiTrendState.NoData
                && !prior.IsBackfilled
            )
            {
                result.Add(new CiTrendBucket(current.BucketStart, prior.State) { IsBackfilled = true });
            }
            else
            {
                result.Add(current);
            }
        }
        return result;
    }

    public static async Task PersistAndPublishAsync(
        IDashboardSnapshotStore store,
        DashboardSnapshotState state,
        DashboardSnapshot snapshot,
        DashboardSnapshot publicSnapshot,
        bool persist,
        ILogger logger,
        CancellationToken ct
    )
    {
        if (persist)
        {
            try
            {
                await store.SaveAsync(snapshot, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist dashboard snapshot; continuing with live in-memory state.");
            }
        }
        else
        {
            logger.LogWarning(
                "Dashboard refresh degraded with no prior snapshot; serving partial data without persisting."
            );
        }

        state.Update(snapshot, publicSnapshot);
    }

    // 24 hourly buckets, oldest first, aligned to UTC clock-hour boundaries. Each
    // bucket shows only what ran in that hour: Failing if any run failed, Passing
    // if any ran without failure, NoData if no runs landed in that window.
    //
    // Buckets are anchored to the top of the hour at or after `now` — not to `now`
    // itself — so a run's bucket is stable across refreshes within the same hour
    // instead of drifting between two adjacent buckets as `now` advances toward the
    // next hour. The newest bucket, index 23, spans the single hour ending at the
    // anchor; when `now` falls exactly on the hour the anchor equals `now`. Runs are
    // binned by their own clock hour, so a run exactly on an hour boundary lands in
    // the bucket it belongs to. Quiet hours between runs show NoData rather than
    // carrying the prior state — use MergeTrends to backfill from a previous snapshot
    // when a refresh is degraded.
    public static IReadOnlyList<CiTrendBucket> BuildCiTrend(IEnumerable<WorkflowRun> runs, Instant now)
    {
        const int buckets = 24;
        const long secondsPerHour = 3600;

        // The clock-hour boundary at or after `now`, as a whole-hour index since the
        // epoch. Rounding up keeps the in-progress hour visible in the newest bucket.
        var nowSeconds = now.ToUnixTimeSeconds();
        var remainder = (nowSeconds % secondsPerHour + secondsPerHour) % secondsPerHour;
        var anchorHour = (nowSeconds - remainder) / secondsPerHour + (remainder == 0 ? 0 : 1);

        var anyRun = new bool[buckets];
        var anyFail = new bool[buckets];
        foreach (var run in runs)
        {
            if (run.UpdatedAt > now)
            {
                continue; // future-dated (clock skew): drop
            }

            var runHour = run.UpdatedAt.ToUnixTimeSeconds() / secondsPerHour;
            // A run in the anchor's preceding hour lands in the newest bucket, index
            // 23; each older hour steps the index down by one. Kept as a long until
            // the range check so a far-past timestamp cannot overflow into range.
            var idx = runHour - anchorHour + buckets;
            if (idx is < 0 or >= buckets)
            {
                continue; // outside the 24h window: drop
            }

            anyRun[idx] = true;
            if (GitHubOrgClient.ToSignalState(run.Status, run.Conclusion) == SignalState.Failure)
            {
                anyFail[idx] = true;
            }
        }

        var result = new List<CiTrendBucket>(buckets);
        for (var i = 0; i < buckets; i++)
        {
            var runState = anyFail[i] ? CiTrendState.Failing : CiTrendState.Passing;
            var state = anyRun[i] ? runState : CiTrendState.NoData;
            var bucketStart = Instant.FromUnixTimeSeconds((anchorHour - (buckets - i)) * secondsPerHour);
            result.Add(new CiTrendBucket(bucketStart, state));
        }
        return result;
    }

    public static IReadOnlyList<SummaryCount> BuildSummary(IReadOnlyList<RepositorySnapshot> repos)
    {
        var workflows = repos.SelectMany(r => r.Workflows).ToList();
        return
        [
            new SummaryCount("repos", repos.Count),
            new SummaryCount("workflows", workflows.Count),
            new SummaryCount("failing", workflows.Count(w => w.State == SignalState.Failure)),
            new SummaryCount("running", workflows.Count(w => w.State == SignalState.Running)),
            new SummaryCount("no-ci", repos.Count(r => r.Workflows.Count == 0)),
            // Null-tolerant: a snapshot restored from a pre-PR on-disk file
            // deserializes PullRequests to null and can be reintroduced via
            // MergeWithPrevious for a degraded repo. Metrics is already guarded below.
            new SummaryCount("open-prs", repos.Sum(r => r.PullRequests is null ? 0 : r.PullRequests.Count)),
            new SummaryCount(
                "nloc-fixportal",
                repos
                    .Where(r =>
                        r.Metrics is not null && !r.Name.Contains("quickfixn", StringComparison.OrdinalIgnoreCase)
                    )
                    .Sum(r => r.Metrics!.Nloc)
            ),
            new SummaryCount(
                "nloc-quickfixn",
                repos
                    .Where(r =>
                        r.Metrics is not null && r.Name.Contains("quickfixn", StringComparison.OrdinalIgnoreCase)
                    )
                    .Sum(r => r.Metrics!.Nloc)
            ),
            new SummaryCount("deploys", repos.Sum(r => r.Deploys is null ? 0 : r.Deploys.Count)),
            new SummaryCount("packages", repos.Sum(r => r.Packages is null ? 0 : r.Packages.Count)),
            new SummaryCount(
                "deploys-failing",
                repos.Sum(r => r.Deploys is null ? 0 : r.Deploys.Count(d => d.State == SignalState.Failure))
            ),
            new SummaryCount(
                "deploys-running",
                repos.Sum(r => r.Deploys is null ? 0 : r.Deploys.Count(d => d.State == SignalState.Running))
            ),
            new SummaryCount(
                "packages-failing",
                repos.Sum(r => r.Packages is null ? 0 : r.Packages.Count(d => d.State == SignalState.Failure))
            ),
        ];
    }
}
