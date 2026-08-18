using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using NodaTime.Text;

// GitHub response records are instantiated and populated by System.Text.Json.
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable NotAccessedPositionalProperty.Global

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

public sealed class GitHubRateLimitException(string message) : Exception(message);

public sealed class GitHubAuthException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record GitHubRepoDto(string Name, string HtmlUrl, bool Private, bool Archived, string? DefaultBranch);

public sealed record GitHubWorkflowDto(long Id, string Name, string Path, string State);

public sealed record GitHubWorkflowsResponse(IReadOnlyList<GitHubWorkflowDto>? Workflows);

public sealed record GitHubRunItem(
    string? Status,
    string? Conclusion,
    string? HtmlUrl,
    string? DisplayTitle,
    int RunNumber,
    string? HeadBranch,
    string? Event,
    Instant UpdatedAt,
    long Id = 0,
    int? RunAttempt = null,
    string? HeadSha = null
);

public sealed record GitHubRunsResponse(IReadOnlyList<GitHubRunItem>? WorkflowRuns);

public sealed record GitHubUserDto(string? Login);

public sealed record GitHubPullHeadDto(string? Sha);

// UpdatedAt and Head are the watermark the incremental enrichment diffs on. Both are
// already in the REST /pulls payload the board fetches every cycle, so reading them
// costs nothing extra — they were simply never deserialized. Optional with defaults so
// every existing construction site (and the merged-PR search path, whose payload has
// neither) keeps compiling.
public sealed record GitHubPullDto(
    int Number,
    string? Title,
    GitHubUserDto? User,
    string? HtmlUrl,
    bool Draft,
    Instant CreatedAt,
    Instant? MergedAt = null,
    Instant? UpdatedAt = null,
    GitHubPullHeadDto? Head = null
);

public sealed record GitHubRunSummary(long Id, string? HtmlUrl, string? Status, string? Conclusion);

public sealed record GitHubRunRawItem(long Id, string? HtmlUrl, string? Status, string? Conclusion);

public sealed record GitHubRunsRawResponse(IReadOnlyList<GitHubRunRawItem>? WorkflowRuns);

public sealed record GitHubJobDto(
    string? Name,
    string? Status,
    string? Conclusion,
    string? HtmlUrl,
    Instant? StartedAt,
    Instant? CompletedAt
);

public sealed record GitHubJobsResponse(IReadOnlyList<GitHubJobDto>? Jobs);

// One scanned run paired with its jobs, fed to the lane selector newest-first.
public sealed record RunWithJobs(GitHubRunSummary Run, IReadOnlyList<GitHubJobDto> Jobs);

// Signals selected so far, plus whether older runs still need scanning.
public sealed record LaneScanResult(IReadOnlyList<JobSignal> Signals, bool Complete);

// Search API — issues/PRs endpoint returns a merged_at field nested under pull_request.
public sealed record GitHubSearchPrInfo(Instant? MergedAt);

public sealed record GitHubSearchIssueDto(
    int Number,
    string? Title,
    GitHubUserDto? User,
    string? HtmlUrl,
    GitHubSearchPrInfo? PullRequest,
    Instant UpdatedAt
);

public sealed record GitHubSearchResponse(IReadOnlyList<GitHubSearchIssueDto>? Items);

public sealed record CodeScanningInstanceDto(string? Ref);

public sealed record CodeScanningAlertDto(CodeScanningInstanceDto? MostRecentInstance);

// Only the count matters, but a payload shape is still needed to deserialize the list.
public sealed record SecretScanningAlertDto(int Number);

public sealed class GitHubOrgClient(
    HttpClient httpClient,
    IOptions<GitHubOptions> gitHub,
    IOptions<DashboardOptions> dashboard,
    GitHubETagStore etags,
    DashboardSnapshotState? state = null,
    ILogger<GitHubOrgClient>? logger = null,
    // Optional, like state and logger above: null means "use the configured personal
    // access token", which is what every unit test wants and what the deployment used
    // before the GitHub App existed.
    IGitHubTokenSource? tokens = null
)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    // GraphQL returns camelCase, unlike the snake_case REST API, so it needs its own
    // options object. It is also a POST, so the ETag store gives it nothing — this
    // request costs a real rate-limit unit on every cycle.
    private static readonly JsonSerializerOptions GraphQlSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string ReviewFactsQuery = """
        query($owner: String!, $name: String!) {
          rateLimit { cost remaining resetAt }
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 25, orderBy: {field: UPDATED_AT, direction: DESC}) {
              pageInfo { hasNextPage }
              nodes {
                number
                author { login }
                labels(first: 20) { nodes { name } }
                reviews(first: 50) { nodes { author { login } commit { oid } } }
                reviewThreads(first: 100) {
                  nodes {
                    isResolved
                    comments(first: 1) {
                      # originalCommit, not commit: commit is the commit the comment
                      # CURRENTLY applies to and can advance as the PR is pushed to;
                      # originalCommit is the commit it was authored against, which is
                      # what head-scoping needs to ask "did this reviewer engage with
                      # the current head?" without a stale thread reporting a false match.
                      nodes { author { login } originalCommit { oid } }
                    }
                  }
                }
                comments(last: 20) {
                  # An issue comment is the only trace a reviewer leaves when it has
                  # nothing to report. `last:` because only the most recent matter, which
                  # is also why truncation shows up as hasPreviousPage, not hasNextPage.
                  nodes { author { login } createdAt lastEditedAt }
                  pageInfo { hasPreviousPage }
                }
                commits(last: 1) {
                  nodes {
                    commit {
                      oid
                      committedDate
                      statusCheckRollup {
                        contexts(first: 50) {
                          nodes { ... on CheckRun { conclusion checkSuite { app { slug } } } }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Selection for one pull request, shared by every alias in an exact-PR query.
    /// Caps are deliberately GENEROUS compared with the per-repo sweep's: cost is priced
    /// by fan-out, and fan-out is now multiplied by the number of CHANGED pull requests
    /// (usually one or two) rather than by 25 slots per repository across 29 repositories.
    /// Fidelity that was unaffordable per-repo is cheap per-PR, and every connection
    /// reports hasNextPage so an over-cap pull request is visible rather than silently
    /// rendering a wrong pill.
    /// </summary>
    private const string ExactPrFragment = """
        fragment PrFacts on PullRequest {
          number
          author { login }
          labels(first: 50) { nodes { name } pageInfo { hasNextPage } }
          reviews(first: 100) { nodes { author { login } commit { oid } } pageInfo { hasNextPage } }
          reviewThreads(first: 100) {
            nodes {
              isResolved
              comments(first: 1) { nodes { author { login } originalCommit { oid } } }
            }
            pageInfo { hasNextPage }
          }
          comments(last: 20) {
            nodes { author { login } createdAt lastEditedAt }
            pageInfo { hasPreviousPage }
          }
          commits(last: 1) {
            nodes {
              commit {
                oid
                committedDate
                statusCheckRollup {
                  contexts(first: 100) {
                    nodes { ... on CheckRun { conclusion checkSuite { app { slug } } } }
                    pageInfo { hasNextPage }
                  }
                }
              }
            }
          }
        }
        """;

    // Exposed for test: the queries are the contract between the mapper and GitHub, and a
    // field silently dropped from one of them starves a collector without failing anything.
    internal static string ReviewFactsQueryText => ReviewFactsQuery;

    internal static string ExactPrFragmentText => ExactPrFragment;

    /// <summary>
    /// Aliases per exact-PR request. Aliasing does not discount anything — fan-out is
    /// still billed per node — so this bounds document size and blast radius on failure,
    /// not cost. One chunk failing loses only its own pull requests.
    /// </summary>
    internal const int ExactPrBatchSize = 20;

    // Search/issues page size. internal so the merged-PR tests size their full-page
    // fixtures off the same value instead of a hand-mirrored literal.
    internal const int SearchPageSize = 20;

    // The reviewThreads connection name as emitted into PrReviewFacts.TruncatedConnections
    // by WarnOnTruncatedConnections. internal and shared with the signal factory (and its
    // tests) because the name is an untyped contract across three files: a drifted literal
    // silently disables the guard that refuses Clean on a truncated thread list.
    internal const string ReviewThreadsConnectionName = "reviewThreads";
    private readonly GitHubOptions _gitHub = gitHub.Value;
    private readonly DashboardOptions _dashboard = dashboard.Value;

    // Repos whose code-scanning endpoint has already been reported as unreadable, so
    // the warning is emitted once rather than on every sweep. Concurrent because
    // nothing guarantees the enrichment sweep is the only caller.
    private readonly ConcurrentDictionary<string, byte> _codeScanningWarned = new(StringComparer.OrdinalIgnoreCase);

    // Same one-warning-per-repo discipline for the secret-scanning endpoint.
    private readonly ConcurrentDictionary<string, byte> _secretScanningWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rate-limit accounting reported by the most recent GraphQL query. GraphQL is
    /// metered on a SEPARATE 5,000-points/hour budget from REST and is priced by
    /// connection fan-out, so the REST request count says nothing about what this
    /// feature costs. Recorded here so the enrichment worker can log the real number
    /// once per sweep instead of the cost being invisible in production.
    /// </summary>
    public GraphQlRateLimit? LastGraphQlRateLimit { get; private set; }

    /// <summary>
    /// One query per repo covering open pull requests. Batched deliberately: a
    /// per-PR query would multiply the request count by the open-PR total and break
    /// the rate budget. affectsAuthState is false throughout — this is a supplementary
    /// signal and a token missing a scope here must not flip /api/health.
    /// The connection is capped at the 25 most recently updated open PRs (first: 25,
    /// no cursor pagination — the nested connections are capped the same way, so every
    /// level of this query trades completeness for a bounded rate cost). The outer cap
    /// is the one that matters for cost: GraphQL prices by connection fan-out, so every
    /// nested connection multiplies by it and halving 50 to 25 roughly halves the whole
    /// query. Trimming the NESTED caps instead would buy the same points while silently
    /// dropping threads and checks — none of them carry a hasNextPage warning, so an
    /// over-cap PR would report a confidently wrong pill. Cut here, not in there.
    /// A repo past
    /// the cap gets no signals for the overflow PRs, which would be invisible without
    /// the hasNextPage warning below.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, PrReviewFacts>> GetPullRequestReviewFactsAsync(
        string repo,
        CancellationToken ct
    )
    {
        var data = await PostGraphQlAsync<ReviewFactsData>(
            ReviewFactsQuery,
            new { owner = _gitHub.Owner, name = repo },
            $"{_gitHub.Owner}/{repo}",
            ct
        );

        LastGraphQlRateLimit = data?.RateLimit;

        if (data?.Repository?.PullRequests?.PageInfo?.HasNextPage == true)
        {
            logger?.LogWarning(
                "{Repo} has more than 25 open pull requests; review signals cover only the 25 most recently updated.",
                repo
            );
        }

        var facts = new Dictionary<int, PrReviewFacts>();
        foreach (var pull in data?.Repository?.PullRequests?.Nodes ?? [])
        {
            facts[pull.Number] = ToReviewFacts(pull);
        }
        return facts;
    }

    /// <summary>
    /// Review facts for named pull requests only — the incremental path. The per-repo
    /// query above asks 25 PR slots per repository on a timer and pays full fan-out for
    /// every empty slot; measured, that was 1,537 points per sweep across 29 repositories
    /// to track 2 open pull requests. This asks about the pull requests that actually
    /// changed, so cost tracks activity rather than estate size and adding repositories
    /// costs nothing.
    /// </summary>
    /// <remarks>
    /// Returns only what GitHub answered for. A requested number missing from the result
    /// means that pull request no longer exists or is no longer visible — the caller
    /// treats that as an eviction, not a failure.
    /// </remarks>
    public async Task<ReviewFactsBatch> GetPullRequestReviewFactsAsync(
        string repo,
        IReadOnlyCollection<int> numbers,
        CancellationToken ct,
        IReadOnlyDictionary<int, Instant>? headFirstSeenAt = null,
        Func<GraphQlRateLimit?, bool>? reserveBreached = null
    )
    {
        if (numbers.Count == 0)
        {
            return ReviewFactsBatch.Empty;
        }

        var facts = new Dictionary<int, PrReviewFacts>();
        var failed = new List<int>();
        var queries = 0;
        var points = 0;

        foreach (var chunk in numbers.Distinct().Order().Chunk(ExactPrBatchSize))
        {
            // The reserve guard is re-evaluated between chunks against the balance every
            // response just returned: checking once per repository would let a twenty-PR
            // retry storm spend straight through the floor the guard exists to protect.
            // Skipped pull requests are marked FAILED, not dropped, so their watermarks
            // hold back and the next sweep retries them.
            if (reserveBreached?.Invoke(LastGraphQlRateLimit) == true)
            {
                failed.AddRange(chunk);
                continue;
            }
            queries++;
            try
            {
                points += await FetchChunkAsync(repo, chunk, facts, ct, headFirstSeenAt);
            }
            // One unreadable pull request must not cost its nineteen neighbours their
            // pills. GitHub answers an aliased query all-or-nothing, so a single refused
            // alias ("Resource not accessible by personal access token") fails the whole
            // document — retrying one at a time isolates the offender and rescues the
            // rest. Only paid when something has already gone wrong, and bounded by the
            // chunk size. GitHubAuthException is deliberately NOT caught here (or below):
            // a 401/403 is credential-wide, so every sibling chunk would fail the same
            // way — it escapes to the worker, which keeps last-known-good for the repo,
            // agreeing with the merge-state path.
            catch (HttpRequestException ex) when (chunk.Length > 1)
            {
                logger?.LogWarning(
                    ex,
                    "Batched review-facts query failed for {Repo} ({Count} pull requests); retrying individually.",
                    repo,
                    chunk.Length
                );
                (var retryQueries, var retryPoints) = await FetchIndividuallyAsync(
                    repo,
                    chunk,
                    facts,
                    failed,
                    ct,
                    headFirstSeenAt,
                    reserveBreached
                );
                queries += retryQueries;
                points += retryPoints;
            }
            catch (HttpRequestException ex)
            {
                failed.AddRange(chunk);
                logger?.LogWarning(ex, "Review facts unavailable for {Repo}#{Number}.", repo, chunk[0]);
            }
        }

        return new ReviewFactsBatch(facts, failed, queries, points);
    }

    /// <summary>
    /// The per-PR rescue loop after a chunked query fails: one pull request's unreadable
    /// alias must not cost its nineteen neighbours their pills. Returns the queries and
    /// points the retries spent.
    /// </summary>
    private async Task<(int Queries, int Points)> FetchIndividuallyAsync(
        string repo,
        IReadOnlyList<int> chunk,
        Dictionary<int, PrReviewFacts> facts,
        List<int> failed,
        CancellationToken ct,
        IReadOnlyDictionary<int, Instant>? headFirstSeenAt,
        Func<GraphQlRateLimit?, bool>? reserveBreached
    )
    {
        var queries = 0;
        var points = 0;
        foreach (var number in chunk)
        {
            // Same between-call reserve check as the chunk loop.
            if (reserveBreached?.Invoke(LastGraphQlRateLimit) == true)
            {
                failed.Add(number);
                continue;
            }
            queries++;
            try
            {
                points += await FetchChunkAsync(repo, [number], facts, ct, headFirstSeenAt);
            }
            catch (HttpRequestException single)
            {
                failed.Add(number);
                logger?.LogWarning(single, "Review facts unavailable for {Repo}#{Number}.", repo, number);
            }
        }
        return (queries, points);
    }

    /// <summary>Fetches one aliased chunk and returns the GraphQL points it cost.</summary>
    private async Task<int> FetchChunkAsync(
        string repo,
        IReadOnlyList<int> chunk,
        Dictionary<int, PrReviewFacts> facts,
        CancellationToken ct,
        IReadOnlyDictionary<int, Instant>? headFirstSeenAt = null
    )
    {
        // Aliases are built from int, so there is no injection surface here.
        var aliases = string.Join("\n    ", chunk.Select(n => $"pr{n}: pullRequest(number: {n}) {{ ...PrFacts }}"));
        var query = $$"""
            query($owner: String!, $name: String!) {
              rateLimit { cost remaining resetAt }
              repository(owner: $owner, name: $name) {
                {{aliases}}
              }
            }
            {{ExactPrFragment}}
            """;

        var data = await PostGraphQlAsync<ExactReviewFactsData>(
            query,
            new { owner = _gitHub.Owner, name = repo },
            $"{_gitHub.Owner}/{repo}",
            ct
        );

        LastGraphQlRateLimit = data?.RateLimit;

        foreach (var pull in data?.Repository?.Values ?? [])
        {
            if (pull is null)
            {
                continue;
            }

            var truncated = WarnOnTruncatedConnections(repo, pull);
            facts[pull.Number] = ToReviewFacts(
                pull,
                // TryGetValue, not GetValueOrDefault: a missing entry must stay null
                // ("anchor unknown" — the comment channel yields nothing), whereas the
                // default(Instant) a missing key would produce is the Unix epoch, which
                // scopes the channel to every comment the pull request ever received.
                headFirstSeenAt is not null
                && headFirstSeenAt.TryGetValue(pull.Number, out var headSince)
                    ? headSince
                    : null,
                truncated.Count > 0 ? truncated : null
            );
        }

        return data?.RateLimit?.Cost ?? 0;
    }

    /// <summary>
    /// GitHub's merge verdict for the given open pull requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four scalars on the pull-request node, so there is no connection fan-out and the
    /// whole batch costs one GraphQL point — unlike the review-facts query, whose cost is
    /// driven by nested thread and comment connections. That is why this is a separate
    /// query rather than extra fields on the existing one: it can be swept far more often
    /// for almost nothing, and it stays alive when review signals are switched off.
    /// </para>
    /// <para>
    /// <c>mergeStateStatus</c> needs no preview Accept header (verified against the live
    /// API on 2026-08-03). It IS computed lazily, so a pull request GitHub has not
    /// evaluated yet answers UNKNOWN; the caller treats that as undetermined and the next
    /// sweep picks it up.
    /// </para>
    /// <para>
    /// Returns only what GitHub answered for. A requested number missing from the result
    /// no longer exists or is no longer visible, which the caller treats as an eviction.
    /// </para>
    /// <para>
    /// Failures PROPAGATE rather than soft-failing per chunk — the deliberate
    /// counterpart to the review-facts path's per-PR isolation. Review facts can afford
    /// to keep a partial result because the worker's watermarks hold back every pull
    /// request that was not fetched, so nothing stale is certified. This method has no
    /// such bookkeeping: a partial dictionary is indistinguishable from a complete one
    /// at the worker, and returning one would replace last-known-good with a silently
    /// incomplete cache. Both methods agree on auth: GitHubAuthException is
    /// credential-wide, so neither retries it per chunk — it escapes to the worker,
    /// which degrades to last-known-good for the whole repo.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, PrMergeState>> GetPullRequestMergeStatesAsync(
        string repo,
        IReadOnlyCollection<int> numbers,
        CancellationToken ct
    )
    {
        var states = new Dictionary<int, PrMergeState>();
        if (numbers.Count == 0)
        {
            return states;
        }

        foreach (var chunk in numbers.Distinct().Order().Chunk(ExactPrBatchSize))
        {
            // Aliases are built from int, so there is no injection surface here.
            var aliases = string.Join(
                "\n    ",
                chunk.Select(n =>
                    $"pr{n}: pullRequest(number: {n}) {{ number isDraft mergeable mergeStateStatus headRefOid }}"
                )
            );
            var query = $$"""
                query($owner: String!, $name: String!) {
                  rateLimit { cost remaining resetAt }
                  repository(owner: $owner, name: $name) {
                    {{aliases}}
                  }
                }
                """;

            var data = await PostGraphQlAsync<MergeStateData>(
                query,
                new { owner = _gitHub.Owner, name = repo },
                $"{_gitHub.Owner}/{repo}",
                ct
            );

            LastGraphQlRateLimit = data?.RateLimit;

            // A null value means GitHub returned no such pull request -- closed since
            // the listing, not an error.
            foreach (var pull in (data?.Repository?.Values ?? []).Where(p => p is not null))
            {
                states[pull!.Number] = new PrMergeState(
                    pull.Number,
                    pull.IsDraft,
                    pull.Mergeable,
                    pull.MergeStateStatus,
                    pull.HeadRefOid
                );
            }
        }

        return states;
    }

    // A truncated nested connection is the one failure this feature cannot absorb: a
    // missing unresolved thread turns Outstanding into a confident Clean. The per-repo
    // query could not afford to detect it; the exact-PR query can, so an over-cap pull
    // request is at least loud rather than quietly wrong. Returns the names of the
    // connections that hit their cap so the caller can carry them onto the facts --
    // logging alone still publishes the incomplete node as authoritative.
    private HashSet<string> WarnOnTruncatedConnections(string repo, ReviewFactsPull pull)
    {
        var truncated = new HashSet<string>(StringComparer.Ordinal);
        if (pull.Labels?.PageInfo?.HasNextPage == true)
        {
            truncated.Add("labels");
        }
        if (pull.Reviews?.PageInfo?.HasNextPage == true)
        {
            truncated.Add("reviews");
        }
        if (pull.ReviewThreads?.PageInfo?.HasNextPage == true)
        {
            truncated.Add(ReviewThreadsConnectionName);
        }
        // hasPreviousPage, not hasNextPage: comments are fetched with `last:`.
        if (pull.Comments?.PageInfo?.HasPreviousPage == true)
        {
            truncated.Add("comments");
        }
        var rollup = pull.Commits?.Nodes?.FirstOrDefault()?.Commit?.StatusCheckRollup;
        if (rollup?.Contexts?.PageInfo?.HasNextPage == true)
        {
            truncated.Add("checkContexts");
        }

        if (truncated.Count > 0)
        {
            logger?.LogWarning(
                "{Repo}#{Number}: {Connections} exceeded the query page size; its review pills are held at Pending until paginated.",
                repo,
                pull.Number,
                string.Join(", ", truncated)
            );
        }
        return truncated;
    }

    private async Task<T?> PostGraphQlAsync<T>(string query, object variables, string subject, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql");
        request.Content = JsonContent.Create(new { query, variables }, options: GraphQlSerializerOptions);
        await AddStandardHeadersAsync(request, ct);

        using var response = await httpClient.SendAsync(request, ct);
        GuardResponse(response, subject, affectsAuthState: false);
        _ = response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<GraphQlEnvelope<T>>(GraphQlSerializerOptions, ct);

        // GraphQL reports failures as HTTP 200 with an errors array. Surfacing them as
        // HttpRequestException means the enrichment worker's existing catch keeps the
        // last-known-good cache rather than writing a partial result.
        if (envelope?.Errors is { Count: > 0 } errors)
        {
            throw new HttpRequestException(
                $"GitHub GraphQL returned {errors.Count} error(s) for {subject}: {errors[0].Message}"
            );
        }

        return envelope is null ? default : envelope.Data;
    }

    public async Task<IReadOnlyList<GitHubRepoDto>> ListRepositoriesAsync(CancellationToken ct)
    {
        var all = new List<GitHubRepoDto>();
        var page = 1;
        while (true)
        {
            var batch =
                await SendAsync<List<GitHubRepoDto>>($"orgs/{_gitHub.Owner}/repos?per_page=100&page={page}", ct) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return _dashboard.ExcludeArchived ? [.. all.Where(r => !r.Archived)] : all;
    }

    public async Task<IReadOnlyList<GitHubWorkflowDto>> ListWorkflowsAsync(string repo, CancellationToken ct)
    {
        var all = new List<GitHubWorkflowDto>();
        var page = 1;
        while (true)
        {
            var response = await SendAsync<GitHubWorkflowsResponse>(
                $"repos/{_gitHub.Owner}/{repo}/actions/workflows?per_page=100&page={page}",
                ct
            );
            var batch = response?.Workflows ?? [];
            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return all.Where(w => IncludeWorkflow(w.Name, w.Path, _dashboard)).ToList();
    }

    public async Task<IReadOnlyList<WorkflowRun>> GetRecentRunsAsync(
        string repo,
        GitHubWorkflowDto workflow,
        CancellationToken ct
    )
    {
        var response = await SendAsync<GitHubRunsResponse>(
            $"repos/{_gitHub.Owner}/{repo}/actions/workflows/{workflow.Id}/runs?per_page={_dashboard.RunHistoryPageSize}",
            ct
        );
        return (response?.WorkflowRuns ?? []).Select(run => ToWorkflowRun(run, repo, workflow)).ToList();
    }

    private WorkflowRun ToWorkflowRun(GitHubRunItem run, string repo, GitHubWorkflowDto workflow) =>
        new(
            run.Status,
            run.Conclusion,
            GetRunUrl(run, repo, workflow),
            string.IsNullOrWhiteSpace(run.DisplayTitle) ? workflow.Name : run.DisplayTitle,
            run.RunNumber,
            run.HeadBranch,
            run.Event,
            run.UpdatedAt,
            // Owner-qualified: the IDE diagnosis route (IdeEndpoints.FindRun) matches on
            // "owner/repo", and the dashboard contract fixture already spells it that way.
            // The bare name here made every diagnosis lookup 404 against a real snapshot.
            $"{_gitHub.Owner}/{repo}",
            FileName(workflow.Path),
            run.Id,
            run.RunAttempt,
            run.HeadSha
        );

    private string GetRunUrl(GitHubRunItem run, string repo, GitHubWorkflowDto workflow)
    {
        if (!string.IsNullOrWhiteSpace(run.HtmlUrl))
        {
            return run.HtmlUrl;
        }

        return run.Id > 0
            ? $"https://github.com/{_gitHub.Owner}/{repo}/actions/runs/{run.Id}"
            : $"https://github.com/{_gitHub.Owner}/{repo}/actions/workflows/{FileName(workflow.Path)}";
    }

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(string repo, CancellationToken ct) =>
        (await ListOpenPullDtosAsync(repo, ct)).Select(p => ToPullRequest(p, _gitHub.Owner, repo)).ToList();

    /// <summary>
    /// The same open-PR listing the board already polls, projected to the watermark the
    /// incremental review enrichment diffs on.
    /// </summary>
    /// <remarks>
    /// This deliberately re-requests the URL rather than plumbing the board's result
    /// through: <see cref="GitHubETagStore"/> caches the decoded payload alongside the
    /// validator, so an unchanged repository answers 304 and replays it, and GitHub does
    /// not charge a conditional 304 against the REST budget. The second caller is
    /// therefore free, and the two consumers cannot race each other into missing a change
    /// — a 304 replays the same payload a 200 would have written.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, PrWatermark>> ListOpenPullRequestWatermarksAsync(
        string repo,
        CancellationToken ct
    ) =>
        (await ListOpenPullDtosAsync(repo, ct)).ToDictionary(
            p => p.Number,
            p => new PrWatermark(p.UpdatedAt, p.Head?.Sha)
        );

    private async Task<IReadOnlyList<GitHubPullDto>> ListOpenPullDtosAsync(string repo, CancellationToken ct)
    {
        var all = new List<GitHubPullDto>();
        var page = 1;
        while (true)
        {
            var batch =
                await SendAsync<List<GitHubPullDto>>(
                    $"repos/{_gitHub.Owner}/{repo}/pulls?state=open&per_page=100&page={page}",
                    ct,
                    affectsAuthState: false
                ) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return all;
    }

    public static PullRequest ToPullRequest(GitHubPullDto dto, string owner, string repo) =>
        new(
            dto.Number,
            string.IsNullOrWhiteSpace(dto.Title) ? $"#{dto.Number}" : dto.Title,
            string.IsNullOrWhiteSpace(dto.User?.Login) ? "unknown" : dto.User!.Login!,
            string.IsNullOrWhiteSpace(dto.HtmlUrl)
                ? $"https://github.com/{owner}/{repo}/pull/{dto.Number}"
                : dto.HtmlUrl!,
            dto.Draft,
            dto.CreatedAt,
            // ReviewSignals/ReadyToMerge stay null here: they are stamped later from the
            // enrichment caches, and only when the cached head matches HeadSha.
            HeadSha: dto.Head?.Sha
        );

    /// <summary>
    /// Extracts the pull-request number from a GitHub ref such as
    /// "refs/pull/181/head". Returns null for any non-pull ref, so branch-level alerts
    /// are ignored rather than mis-attributed to a pull request.
    /// </summary>
    public static int? PullNumberFromRef(string? gitRef)
    {
        if (string.IsNullOrEmpty(gitRef))
        {
            return null;
        }
        const string prefix = "refs/pull/";
        if (!gitRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = gitRef.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var numberSpan = slash < 0 ? rest : rest[..slash];
        return int.TryParse(numberSpan, out var number) ? number : null;
    }

    /// <summary>
    /// Open code-scanning alerts for a repo, bucketed by pull-request number. One call
    /// per repo, not per pull request. Returns NULL when the endpoint cannot be read —
    /// the token lacks "Code scanning alerts: read", or scanning is not enabled — which
    /// the caller must render as "not yet reviewed", never as a clean scan. An empty
    /// dictionary means the endpoint answered and nothing is open.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>?> GetOpenCodeScanningAlertCountsAsync(
        string repo,
        CancellationToken ct
    )
    {
        var all = new List<CodeScanningAlertDto>();
        var page = 1;
        while (true)
        {
            List<CodeScanningAlertDto>? batch;
            try
            {
                // affectsAuthState: false — a missing code-scanning scope 403s here and must
                // not flip /api/health, exactly as the pull-request listing does.
                batch = await SendAsync<List<CodeScanningAlertDto>>(
                    $"repos/{_gitHub.Owner}/{repo}/code-scanning/alerts?state=open&per_page=100&page={page}",
                    ct,
                    affectsAuthState: false
                );
            }
            catch (GitHubAuthException ex)
            {
                // Once per repo per process, not per cycle: a missing "Code scanning
                // alerts: read" scope is permanent until an operator fixes the PAT, and
                // silently swallowing it leaves the CodeQL pill stuck on Pending with no
                // clue why.
                if (_codeScanningWarned.TryAdd(repo, 0))
                {
                    logger?.LogWarning(
                        ex,
                        "Code-scanning alerts are unreadable for {Repo}; the CodeQL reviewer will stay Pending until the PAT gains \"Code scanning alerts: read\" (or scanning is enabled on the repo).",
                        repo
                    );
                }
                return null;
            }

            // The endpoint answered, so any earlier one-time warning is stale: clear it
            // so a permission regression later in the process re-warns, and make the
            // recovery visible rather than just the absence of a warning.
            if (_codeScanningWarned.TryRemove(repo, out _))
            {
                logger?.LogInformation(
                    "Code-scanning alerts are readable again for {Repo}; the earlier permission warning has cleared.",
                    repo
                );
            }

            // SendAsync maps 404 to default(T) — repo has scanning disabled entirely.
            if (batch is null)
            {
                return null;
            }

            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return Bucket(all);
    }

    /// <summary>
    /// Count of open secret-scanning alerts for a repo. Returns NULL when the endpoint
    /// cannot be read — the token lacks "Secret scanning alerts: read", or the product is
    /// not enabled on this repository (private repos, where it is paid) — which the caller
    /// must render as "not yet reviewed", never as clean. Zero means the endpoint answered
    /// and nothing is open.
    /// </summary>
    /// <remarks>
    /// Repo-scoped and not bucketed by pull request, unlike the code-scanning counterpart:
    /// the REST route accepts no ref filter, so there is nothing to attribute an alert to
    /// a pull request with.
    /// </remarks>
    public async Task<int?> GetOpenSecretScanningAlertCountAsync(string repo, CancellationToken ct)
    {
        var total = 0;
        var page = 1;
        while (true)
        {
            List<SecretScanningAlertDto>? batch;
            try
            {
                // affectsAuthState: false — a missing secret-scanning scope 403s here and
                // must not flip /api/health, exactly as the code-scanning listing does.
                batch = await SendAsync<List<SecretScanningAlertDto>>(
                    $"repos/{_gitHub.Owner}/{repo}/secret-scanning/alerts?state=open&per_page=100&page={page}",
                    ct,
                    affectsAuthState: false
                );
            }
            catch (GitHubAuthException ex)
            {
                if (_secretScanningWarned.TryAdd(repo, 0))
                {
                    logger?.LogWarning(
                        ex,
                        "Secret-scanning alerts are unreadable for {Repo}; the Secret Scanning reviewer will stay Pending until the PAT gains \"Secret scanning alerts: read\" (or the product is enabled on the repo).",
                        repo
                    );
                }
                return null;
            }

            if (_secretScanningWarned.TryRemove(repo, out _))
            {
                logger?.LogInformation(
                    "Secret-scanning alerts are readable again for {Repo}; the earlier permission warning has cleared.",
                    repo
                );
            }

            // SendAsync maps 404 to default(T) — the product is off for this repository.
            // Page 1 cannot distinguish "off" from "empty", so it stays unknown; a later
            // page going missing means the listing ended, and what was counted stands.
            if (batch is null)
            {
                return page == 1 ? null : total;
            }

            total += batch.Count;
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return total;
    }

    private static Dictionary<int, int> Bucket(IReadOnlyList<CodeScanningAlertDto> alerts)
    {
        var counts = new Dictionary<int, int>();
        foreach (var alert in alerts)
        {
            if (PullNumberFromRef(alert.MostRecentInstance?.Ref) is { } number)
            {
                counts[number] = counts.GetValueOrDefault(number) + 1;
            }
        }
        return counts;
    }

    /// <summary>
    /// Flattens one GraphQL pull-request node into the facts the signal factory needs.
    /// Every collection is treated as optional: GraphQL omits or nulls empty
    /// connections, and a missing author is legitimate for a deleted account.
    /// All string sets are case-insensitive so a configured BotLogin cannot miss on
    /// casing alone (GitHub logins are case-preserving but not case-sensitive).
    /// </summary>
    /// <param name="headFirstSeenAt">
    /// When the enrichment worker first observed the current head SHA, scoping the
    /// issue-comment channel (see CollectHeadCommentAuthors). Null scopes that channel
    /// to nothing.
    /// </param>
    /// <param name="truncated">
    /// Connections that hit their page cap for this pull request, collected by
    /// WarnOnTruncatedConnections. Carried onto the facts so the signal factory can
    /// refuse Clean on evidence it cannot prove complete.
    /// </param>
    public static PrReviewFacts ToReviewFacts(
        ReviewFactsPull pull,
        Instant? headFirstSeenAt = null,
        IReadOnlySet<string>? truncated = null
    )
    {
        var labels = CollectLabels(pull.Labels);
        var headOid = GetHeadOid(pull.Commits);
        var headParticipating = CollectReviewers(pull.Reviews, headOid);
        var unresolved = CollectThreadFacts(pull.ReviewThreads, headParticipating, headOid);
        var headComments = CollectHeadCommentAuthors(pull.Comments, pull.Commits, headFirstSeenAt);
        var checkApps = CollectSuccessfulCheckApps(pull.Commits);

        return new PrReviewFacts(
            pull.Number,
            pull.Author?.Login is { Length: > 0 } author ? author : "unknown",
            labels,
            unresolved,
            headParticipating,
            headComments,
            checkApps,
            headOid,
            truncated
        );
    }

    private static HashSet<string> CollectLabels(NodeList<GraphQlLabel>? labels)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in (labels?.Nodes ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Name)))
        {
            _ = result.Add(label.Name!);
        }
        return result;
    }

    // commits(last: 1) requests exactly the head commit, so the last (only) node is it.
    // A null result (empty/absent commits connection) means the caller must not assume
    // any participation matches — see CollectReviewers / CollectThreadFacts.
    private static string? GetHeadOid(NodeList<GraphQlCommitNode>? commits) =>
        commits?.Nodes is { Count: > 0 } nodes ? nodes[^1].Commit?.Oid : null;

    private static bool IsHeadCommit(string? oid, string? headOid) =>
        headOid is not null && string.Equals(oid, headOid, StringComparison.Ordinal);

    // An issue comment carries no commit, so "did this land on the current head?" becomes
    // a timestamp comparison. The anchor is when the head was first SEEN at its current
    // SHA (headFirstSeenAt, tracked by the enrichment worker from watermark head-SHA
    // transitions) -- an upper bound on the push instant -- NOT the head commit's
    // committedDate, which is when the commit was AUTHORED. Authoring precedes pushing, so
    // committedDate would certify a comment written after authoring but before the push:
    // a false Clean on a head the reviewer never saw (also reachable by force-push to an
    // older commit, or a cherry-pick). headFirstSeenAt null means the transition instant
    // is unknown for this pull request, and then this channel yields NOTHING -- weaker
    // evidence must not reach Clean on its own when it cannot be scoped. The committedDate
    // comparison is kept as a second, redundant bound. Strict > both ways: a comment
    // stamped equal to either anchor is ambiguous, and Pending is the safe direction.
    // Anything unreadable -- no head date, no timestamp, an unparseable one -- drops the
    // author, never promotes them.
    private static HashSet<string> CollectHeadCommentAuthors(
        NodeList<GraphQlIssueComment>? comments,
        NodeList<GraphQlCommitNode>? commits,
        Instant? headFirstSeenAt
    )
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (headFirstSeenAt is not { } headSince || GetHeadCommittedAt(commits) is not { } headAt)
        {
            return result;
        }
        foreach (
            var login in (comments?.Nodes ?? [])
                .Where(c => LastSpokeAt(c) is { } spokeAt && spokeAt > headAt && spokeAt > headSince)
                .Select(c => c.Author?.Login)
                .OfType<string>()
                .Where(login => login.Length > 0)
        )
        {
            _ = result.Add(login);
        }
        return result;
    }

    // When the comment last SAID something, which is its edit if it has one. A reviewer
    // that keeps one dashboard comment and rewrites it (Gitar) creates that comment
    // seconds after the pull request opens -- before its own review has run -- and edits
    // the verdict in minutes later. Dating it by CreatedAt reads the placeholder and
    // pins the reviewer to Pending forever, because no second comment ever arrives.
    // Editing does not change authorship, so this is evidence about the same bot; the
    // head bounds in CollectHeadCommentAuthors still decide whether it covers this head.
    private static Instant? LastSpokeAt(GraphQlIssueComment comment)
    {
        var created = ParseInstant(comment.CreatedAt);
        var edited = ParseInstant(comment.LastEditedAt);
        if (created is { } c && edited is { } e)
        {
            return Instant.Max(c, e);
        }
        return edited ?? created;
    }

    private static Instant? GetHeadCommittedAt(NodeList<GraphQlCommitNode>? commits) =>
        commits?.Nodes is { Count: > 0 } nodes ? ParseInstant(nodes[^1].Commit?.CommittedDate) : null;

    // GitHub returns ISO-8601 UTC ("2026-08-03T10:35:48Z"). A malformed value is a fact we
    // cannot read, not an exception: one bad timestamp must not take out a whole sweep.
    private static Instant? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var parsed = InstantPattern.ExtendedIso.Parse(value);
        return parsed.Success ? parsed.Value : null;
    }

    private static HashSet<string> CollectReviewers(NodeList<GraphQlReview>? reviews, string? headOid)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews?.Nodes ?? [])
        {
            if (review.Author?.Login is { Length: > 0 } login && IsHeadCommit(review.Commit?.Oid, headOid))
            {
                _ = result.Add(login);
            }
        }
        return result;
    }

    // Unresolved-thread counting is commit-agnostic: an open finding from an older
    // commit is still open. Head participation is not — see IsHeadCommit.
    private static Dictionary<string, int> CollectThreadFacts(
        NodeList<GraphQlThread>? threads,
        HashSet<string> headParticipating,
        string? headOid
    )
    {
        var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var thread in threads?.Nodes ?? [])
        {
            // The FIRST comment's author owns the thread. A human replying to a bot's
            // finding must not re-attribute that thread to the human.
            var comment = thread.Comments?.Nodes is { Count: > 0 } comments ? comments[0] : null;
            if (comment?.Author?.Login is not { Length: > 0 } author)
            {
                continue;
            }
            if (IsHeadCommit(comment.OriginalCommit?.Oid, headOid))
            {
                _ = headParticipating.Add(author);
            }
            if (!thread.IsResolved)
            {
                unresolved[author] = unresolved.GetValueOrDefault(author) + 1;
            }
        }
        return unresolved;
    }

    private static HashSet<string> CollectSuccessfulCheckApps(NodeList<GraphQlCommitNode>? commits)
    {
        var checkApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits?.Nodes ?? [])
        {
            foreach (var context in commit.Commit?.StatusCheckRollup?.Contexts?.Nodes ?? [])
            {
                if (
                    string.Equals(context.Conclusion, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                    && context.CheckSuite?.App?.Slug is { Length: > 0 } slug
                )
                {
                    _ = checkApps.Add(slug);
                }
            }
        }
        return checkApps;
    }

    public async Task<MergedPullRequest?> GetLastMergedPullRequestAsync(string repo, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"repo:{_gitHub.Owner}/{repo} is:pr is:merged");
        var page = 1;
        var perPage = SearchPageSize;
        Instant? maxMergedAt = null;
        GitHubSearchIssueDto? bestItem = null;

        while (true)
        {
            if (page > 50)
            {
                logger?.LogWarning(
                    "Search PR pagination reached the 1000-result (50 pages) cap for repo {Repo}; stopping early.",
                    repo
                );
                break;
            }

            var response = await SendAsync<GitHubSearchResponse>(
                $"search/issues?q={q}&sort=updated&order=desc&per_page={perPage}&page={page}",
                ct,
                affectsAuthState: false
            );
            var items = response?.Items ?? [];
            if (items.Count == 0)
            {
                break;
            }

            UpdateLatestMerged(items, ref maxMergedAt, ref bestItem);

            var lastItem = items[^1];
            // Termination: since subsequent items have updated_at <= lastItem.UpdatedAt,
            // their merged_at cannot exceed lastItem.UpdatedAt.
            // If our maxMergedAt is >= lastItem.UpdatedAt, we are guaranteed to have found the true latest merged PR.
            if (maxMergedAt is not null && maxMergedAt.Value >= lastItem.UpdatedAt)
            {
                break;
            }

            if (items.Count < perPage)
            {
                break;
            }

            page++;
        }

        if (bestItem is null)
        {
            return null;
        }

        return new MergedPullRequest(
            bestItem.Number,
            string.IsNullOrWhiteSpace(bestItem.Title) ? $"#{bestItem.Number}" : bestItem.Title,
            string.IsNullOrWhiteSpace(bestItem.User?.Login) ? "unknown" : bestItem.User!.Login!,
            repo,
            string.IsNullOrWhiteSpace(bestItem.HtmlUrl)
                ? $"https://github.com/{_gitHub.Owner}/{repo}/pull/{bestItem.Number}"
                : bestItem.HtmlUrl!,
            maxMergedAt!.Value
        );
    }

    private static void UpdateLatestMerged(
        IReadOnlyList<GitHubSearchIssueDto> items,
        ref Instant? maxMergedAt,
        ref GitHubSearchIssueDto? bestItem
    )
    {
        var pageBest = items.Where(i => i.PullRequest?.MergedAt is not null).MaxBy(i => i.PullRequest!.MergedAt!.Value);
        if (pageBest is null)
        {
            return;
        }

        var pageMax = pageBest.PullRequest!.MergedAt!.Value;
        if (maxMergedAt is null || pageMax > maxMergedAt.Value)
        {
            maxMergedAt = pageMax;
            bestItem = pageBest;
        }
    }

    public async Task<IReadOnlyList<GitHubRunSummary>> GetRecentDefaultBranchRunsAsync(
        string repo,
        long workflowId,
        string branch,
        int count,
        int page,
        CancellationToken ct
    )
    {
        var response = await SendAsync<GitHubRunsRawResponse>(
            $"repos/{_gitHub.Owner}/{repo}/actions/workflows/{workflowId}/runs?branch={Uri.EscapeDataString(branch)}&per_page={count}&page={page}",
            ct
        );
        return (response?.WorkflowRuns ?? [])
            .Select(r => new GitHubRunSummary(r.Id, r.HtmlUrl, r.Status, r.Conclusion))
            .ToList();
    }

    public async Task<IReadOnlyList<GitHubJobDto>> GetRunJobsAsync(string repo, long runId, CancellationToken ct)
    {
        var all = new List<GitHubJobDto>();
        var page = 1;
        while (true)
        {
            var response = await SendAsync<GitHubJobsResponse>(
                $"repos/{_gitHub.Owner}/{repo}/actions/runs/{runId}/jobs?per_page=100&page={page}",
                ct
            );
            var batch = response?.Jobs ?? [];
            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return all;
    }

    public static bool IsJobMatch(string name, IReadOnlyList<string> patterns) =>
        patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

    // GitHub renders a reusable-workflow / matrix job as "caller / called", which for
    // a deploy job is usually the same segment twice ("Deploy (x) / Deploy (x)").
    // Collapse consecutive identical segments so the gated job's run form and its
    // skipped form ("Deploy (x)") resolve to one stable target key. Mirrors the
    // frontend dedupeJobLabel so the chip and the selector agree on identity.
    public static string CanonicalJobTarget(string name)
    {
        var parts = name.Split(" / ");
        var canonical = parts.Where(
            (part, index) => index == 0 || !string.Equals(parts[index - 1], part, StringComparison.Ordinal)
        );
        return string.Join(" / ", canonical);
    }

    // Surface the newest *definite* signal for each distinct deploy target across the
    // scanned runs (newest-first). A gated prod deploy is skipped in most runs (Unknown)
    // while the ungated dev deploy succeeds every run; selecting per-target — rather than
    // returning the first run that has any signal — stops the dev deploy from shadowing
    // prod's last real run. <see cref="LaneScanResult.Complete"/> lets the caller stop
    // fetching older runs once every seen target has a signal (or the workflow runs no
    // matching job at all), bounding the extra jobs-API calls.
    public static LaneScanResult SelectLaneSignals(
        string workflowName,
        string repoFallbackUrl,
        IReadOnlyList<RunWithJobs> runsNewestFirst,
        IReadOnlyList<string> patterns
    )
    {
        var order = new List<string>(); // canonical target keys, first-seen order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // O(1) dedup
        var signals = new Dictionary<string, JobSignal>(StringComparer.OrdinalIgnoreCase);

        foreach (var rwj in runsNewestFirst)
        {
            foreach (
                var job in rwj.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.Name) && IsJobMatch(j.Name!, patterns))
            )
            {
                RecordJob(job, rwj.Run, workflowName, repoFallbackUrl, order, seen, signals);
            }
        }

        var ordered = order.Where(signals.ContainsKey).Select(k => signals[k]).ToList();
        return new LaneScanResult(ordered, IsScanComplete(runsNewestFirst, order, signals));
    }

    // Add a deploy-matched job to the per-target accumulators. Records the canonical
    // target in first-seen order; keeps the first (newest, scan is newest-first) definite
    // state per target and ignores Unknown (skipped / neutral / not-yet-run).
    private static void RecordJob(
        GitHubJobDto job,
        GitHubRunSummary run,
        string workflowName,
        string repoFallbackUrl,
        List<string> order,
        HashSet<string> seen,
        Dictionary<string, JobSignal> signals
    )
    {
        var key = CanonicalJobTarget(job.Name!);
        if (seen.Add(key))
        {
            order.Add(key); // HashSet.Add returns true on first insert
        }

        var state = ToSignalState(job.Status, job.Conclusion);
        if (state == SignalState.Unknown || signals.ContainsKey(key))
        {
            return;
        }

        signals[key] = new JobSignal(
            workflowName,
            job.Name!,
            state,
            JobUrl(job, run, repoFallbackUrl),
            job.CompletedAt ?? job.StartedAt ?? Instant.MinValue
        );
    }

    private static string JobUrl(GitHubJobDto job, GitHubRunSummary run, string repoFallbackUrl)
    {
        if (!string.IsNullOrWhiteSpace(job.HtmlUrl))
        {
            return job.HtmlUrl!;
        }

        return string.IsNullOrWhiteSpace(run.HtmlUrl) ? repoFallbackUrl : run.HtmlUrl!;
    }

    // The caller can stop fetching older runs once every target it has seen carries a
    // signal. With no matching job at all, a completed newest run means this workflow
    // doesn't run the lane (stop); an in-progress newest run may not have created its
    // gated job yet (keep looking back). A zero-job completed run is treated as
    // inconclusive so prior deploy signals are not replaced with an empty list.
    //
    // A target that is *always* skipped (a gated prod deploy that never runs on the
    // default branch) is added to `order` but never gets a signal, so this never
    // returns true and the caller scans back to MaxRunsToScan every sweep. That is
    // intentional, not a leak: a target skipped in recent runs may have a real signal
    // further back, and the only way to surface it is to keep looking. The repeated
    // look-back is also nearly free — completed runs are immutable, so their jobs
    // endpoint revalidates as a 304 via the ETag cache, which GitHub does not charge
    // against the rate budget. The bound is MaxRunsToScan.
    private static bool IsScanComplete(
        IReadOnlyList<RunWithJobs> runsNewestFirst,
        List<string> order,
        Dictionary<string, JobSignal> signals
    )
    {
        if (order.Count == 0)
        {
            if (runsNewestFirst.Count == 0)
            {
                return false;
            }

            var newest = runsNewestFirst[0];
            // Only stop when the newest completed run actually had jobs; a zero-job
            // run has no evidence the workflow ran the deploy lane, so keep looking.
            return newest.Run.Status == "completed" && newest.Jobs.Count > 0;
        }
        return order.All(signals.ContainsKey);
    }

    public static SignalState ToSignalState(WorkflowRun? run) =>
        run is null ? SignalState.Unknown : ToSignalState(run.Status, run.Conclusion);

    // Core mapper, shared by workflow runs and individual jobs (same status /
    // conclusion vocabulary in the GitHub API).
    public static SignalState ToSignalState(string? status, string? conclusion)
    {
        if (conclusion is null)
        {
            return status is "in_progress" or "queued" or "requested" or "waiting" or "pending"
                ? SignalState.Running
                : SignalState.Unknown;
        }
        return conclusion switch
        {
            "success" => SignalState.Success,
            "failure" or "timed_out" or "startup_failure" => SignalState.Failure,
            _ => SignalState.Unknown,
        };
    }

    public static bool IncludeWorkflow(string name, string path, DashboardOptions options)
    {
        var file = FileName(path);
        // Broad filter: excludes any path containing "dependabot" (case-insensitive)
        // to skip both synthetic Dependabot run entries and custom user workflows.
        if (path.Contains("dependabot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isReusable = file.StartsWith('_');
        if (isReusable && !options.IncludeReusable)
        {
            return false;
        }

        var isCodeQl =
            path.Contains("github-code-scanning", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CodeQL", StringComparison.OrdinalIgnoreCase);
        if (isCodeQl && !options.IncludeCodeQl)
        {
            return false;
        }

        return true;
    }

    public static string FileName(string path) => Path.GetFileName(path);

    public async Task<HttpResponseMessage> DownloadRunLogsAsync(
        HttpClient downloadClient,
        string repo,
        long runId,
        int attempt,
        CancellationToken ct
    )
    {
        // The attempt-scoped route, not /runs/{id}/logs: the unscoped route silently
        // serves the LATEST attempt, so after a re-run the diagnosis would assert the
        // snapshot's attempt and SHA over logs from a newer one.
        var path = $"repos/{_gitHub.Owner}/{repo}/actions/runs/{runId}/attempts/{attempt}/logs";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(httpClient.BaseAddress!, path));
        await AddStandardHeadersAsync(request, ct);

        var response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            GuardResponse(response, path, affectsAuthState: false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // affectsAuthState gates whether this request drives the global auth-error
    // health signal. Primary endpoints (repos/workflows/runs) set it on a 401/403
    // so a worker-path failure between refresh cycles still surfaces on /api/health.
    // A success no longer clears it here: with up to MaxParallelRepos fetches in
    // flight, a healthy repo's success used to race-clear a failing sibling's error,
    // masking it. For the refresh path the authoritative value is instead published
    // once per cycle by DashboardRefreshService from that cycle's aggregated auth
    // outcomes, so a per-repo failure can no longer be cleared by a concurrent success.
    // Best-effort PR endpoints pass false: a token missing only the
    // "Pull requests: Read" scope 403s there, and treating that as a global auth
    // error flipped /api/health to Degraded — a flap. Such a request still throws
    // GitHubAuthException (its caller swallows it and shows no PRs); it just never
    // touches the shared health state.
    private async Task<T?> SendAsync<T>(string url, CancellationToken ct, bool affectsAuthState = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddStandardHeadersAsync(request, ct);

        // Conditional GET: a matching validator yields 304 Not Modified, which GitHub
        // serves without charging the primary rate limit. We then return the payload
        // decoded from the prior 200 rather than re-fetching it.
        var cached = etags.Get(url);
        if (cached is not null)
        {
            request.Headers.IfNoneMatch.Add(cached.ETag);
        }

        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return (T?)cached.Payload;
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        GuardResponse(response, url, affectsAuthState);
        _ = response.EnsureSuccessStatusCode();

        var value = await response.Content.ReadFromJsonAsync<T>(SerializerOptions, ct);

        // Remember the validator + payload so the next cycle can revalidate cheaply.
        if (response.Headers.ETag is { } etag)
        {
            etags.Set(url, etag, value);
        }

        return value;
    }

    // Async because a GitHub App installation token expires hourly and is re-minted on
    // demand; a PAT resolves synchronously and this simply completes.
    private async Task AddStandardHeadersAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "fixportal-ci-backend");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Authorization = new("Bearer", tokens is null ? _gitHub.Token : await tokens.GetTokenAsync(ct));
    }

    private void GuardResponse(HttpResponseMessage response, string url, bool affectsAuthState)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var err =
                $"GitHub authentication failed (HTTP 401 Unauthorized) for {url}. Verify the configured PAT token.";
            if (affectsAuthState)
            {
                state?.SetAuthError(err);
            }
            throw new GitHubAuthException(err);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden && !IsRateLimited(response))
        {
            var err = $"GitHub authorization failed (HTTP 403 Forbidden) for {url}. Verify SSO or scopes.";
            if (affectsAuthState)
            {
                state?.SetAuthError(err);
            }
            throw new GitHubAuthException(err);
        }

        // A 429 is unconditional proof of rate limiting, so treat it as rate-limited
        // even when it carries neither X-RateLimit-Remaining:0 nor Retry-After —
        // otherwise it fell through to a plain HttpRequestException and did not abort
        // the sibling parallel fetches. The header check only disambiguates a 403,
        // which is genuinely ambiguous between auth failure and rate limit.
        if (
            response.StatusCode == HttpStatusCode.TooManyRequests
            || response.StatusCode == HttpStatusCode.Forbidden && IsRateLimited(response)
        )
        {
            throw new GitHubRateLimitException(
                $"GitHub rate limit reached (HTTP {(int)response.StatusCode}) for {url}."
            );
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault()?.Trim(), out var left)
            && left == 0
        )
        {
            return true;
        }
        return response.Headers.Contains("Retry-After");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        _ = o.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        return o;
    }
}
