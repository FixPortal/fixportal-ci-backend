using System.Net;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

public sealed class GitHubRateLimitException(string message) : Exception(message);
public sealed class GitHubAuthException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record GitHubRepoDto(string Name, string HtmlUrl, bool Private, bool Archived, string? DefaultBranch);
public sealed record GitHubWorkflowDto(long Id, string Name, string Path, string State);
public sealed record GitHubWorkflowsResponse(IReadOnlyList<GitHubWorkflowDto>? Workflows);
public sealed record GitHubRunItem(
    string? Status, string? Conclusion, string? HtmlUrl, string? DisplayTitle,
    int RunNumber, string? HeadBranch, string? Event, Instant UpdatedAt);
public sealed record GitHubRunsResponse(IReadOnlyList<GitHubRunItem>? WorkflowRuns);
public sealed record GitHubUserDto(string? Login);
public sealed record GitHubPullDto(
    int Number, string? Title, GitHubUserDto? User, string? HtmlUrl, bool Draft, Instant CreatedAt, Instant? MergedAt = null);
public sealed record GitHubRunSummary(long Id, string? HtmlUrl, string? Status, string? Conclusion);
public sealed record GitHubRunRawItem(long Id, string? HtmlUrl, string? Status, string? Conclusion);
public sealed record GitHubRunsRawResponse(IReadOnlyList<GitHubRunRawItem>? WorkflowRuns);
public sealed record GitHubJobDto(
    string? Name, string? Status, string? Conclusion, string? HtmlUrl, Instant? StartedAt, Instant? CompletedAt);
public sealed record GitHubJobsResponse(IReadOnlyList<GitHubJobDto>? Jobs);

// One scanned run paired with its jobs, fed to the lane selector newest-first.
public sealed record RunWithJobs(GitHubRunSummary Run, IReadOnlyList<GitHubJobDto> Jobs);
// Signals selected so far, plus whether older runs still need scanning.
public sealed record LaneScanResult(IReadOnlyList<JobSignal> Signals, bool Complete);

// Search API — issues/PRs endpoint returns a merged_at field nested under pull_request.
public sealed record GitHubSearchPrInfo(Instant? MergedAt);
public sealed record GitHubSearchIssueDto(
    int Number, string? Title, GitHubUserDto? User, string? HtmlUrl,
    GitHubSearchPrInfo? PullRequest, Instant UpdatedAt);
public sealed record GitHubSearchResponse(IReadOnlyList<GitHubSearchIssueDto>? Items);

public sealed class GitHubOrgClient(
    HttpClient httpClient,
    IOptions<GitHubOptions> gitHub,
    IOptions<DashboardOptions> dashboard,
    GitHubETagStore etags,
    DashboardSnapshotState? state = null,
    ILogger<GitHubOrgClient>? logger = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    // Search/issues page size. internal so the merged-PR tests size their full-page
    // fixtures off the same value instead of a hand-mirrored literal.
    internal const int SearchPageSize = 20;
    private readonly GitHubOptions _gitHub = gitHub.Value;
    private readonly DashboardOptions _dashboard = dashboard.Value;
    private readonly GitHubETagStore _etags = etags;

    public async Task<IReadOnlyList<GitHubRepoDto>> ListRepositoriesAsync(CancellationToken ct)
    {
        var all = new List<GitHubRepoDto>();
        var page = 1;
        while (true)
        {
            var batch = await SendAsync<List<GitHubRepoDto>>(
                $"orgs/{_gitHub.Owner}/repos?per_page=100&page={page}", ct) ?? [];
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
                $"repos/{_gitHub.Owner}/{repo}/actions/workflows?per_page=100&page={page}", ct);
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

    public async Task<IReadOnlyList<WorkflowRun>> GetRecentRunsAsync(string repo, GitHubWorkflowDto workflow, CancellationToken ct)
    {
        var response = await SendAsync<GitHubRunsResponse>(
            $"repos/{_gitHub.Owner}/{repo}/actions/workflows/{workflow.Id}/runs?per_page={_dashboard.RunHistoryPageSize}", ct);
        return (response?.WorkflowRuns ?? [])
            .Select(run => ToWorkflowRun(run, repo, workflow))
            .ToList();
    }

    private WorkflowRun ToWorkflowRun(GitHubRunItem run, string repo, GitHubWorkflowDto workflow) =>
        new(run.Status, run.Conclusion,
            string.IsNullOrWhiteSpace(run.HtmlUrl)
                ? $"https://github.com/{_gitHub.Owner}/{repo}/actions/workflows/{FileName(workflow.Path)}"
                : run.HtmlUrl,
            string.IsNullOrWhiteSpace(run.DisplayTitle) ? workflow.Name : run.DisplayTitle,
            run.RunNumber, run.HeadBranch, run.Event, run.UpdatedAt,
            repo,
            FileName(workflow.Path));

    public async Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(string repo, CancellationToken ct)
    {
        var all = new List<GitHubPullDto>();
        var page = 1;
        while (true)
        {
            var batch = await SendAsync<List<GitHubPullDto>>(
                $"repos/{_gitHub.Owner}/{repo}/pulls?state=open&per_page=100&page={page}", ct,
                affectsAuthState: false) ?? [];
            all.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }

            page++;
        }
        return all.Select(p => ToPullRequest(p, _gitHub.Owner, repo)).ToList();
    }

    public static PullRequest ToPullRequest(GitHubPullDto dto, string owner, string repo) =>
        new(dto.Number,
            string.IsNullOrWhiteSpace(dto.Title) ? $"#{dto.Number}" : dto.Title,
            string.IsNullOrWhiteSpace(dto.User?.Login) ? "unknown" : dto.User!.Login!,
            string.IsNullOrWhiteSpace(dto.HtmlUrl)
                ? $"https://github.com/{owner}/{repo}/pull/{dto.Number}"
                : dto.HtmlUrl!,
            dto.Draft,
            dto.CreatedAt);

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
                logger?.LogWarning("Search PR pagination reached the 1000-result (50 pages) cap for repo {Repo}; stopping early.", repo);
                break;
            }

            var response = await SendAsync<GitHubSearchResponse>(
                $"search/issues?q={q}&sort=updated&order=desc&per_page={perPage}&page={page}", ct,
                affectsAuthState: false);
            var items = response?.Items ?? [];
            if (items.Count == 0)
            {
                break;
            }

            var pageBest = items
                .Where(i => i.PullRequest?.MergedAt is not null)
                .MaxBy(i => i.PullRequest!.MergedAt!.Value);

            if (pageBest is not null)
            {
                var pageMax = pageBest.PullRequest!.MergedAt!.Value;
                if (maxMergedAt is null || pageMax > maxMergedAt.Value)
                {
                    maxMergedAt = pageMax;
                    bestItem = pageBest;
                }
            }

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
            maxMergedAt!.Value);
    }

    public async Task<IReadOnlyList<GitHubRunSummary>> GetRecentDefaultBranchRunsAsync(
        string repo, long workflowId, string branch, int count, int page, CancellationToken ct)
    {
        var response = await SendAsync<GitHubRunsRawResponse>(
            $"repos/{_gitHub.Owner}/{repo}/actions/workflows/{workflowId}/runs?branch={Uri.EscapeDataString(branch)}&per_page={count}&page={page}", ct);
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
                $"repos/{_gitHub.Owner}/{repo}/actions/runs/{runId}/jobs?per_page=100&page={page}", ct);
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
        var canonical = parts.Where((part, index) =>
            index == 0 || !string.Equals(parts[index - 1], part, StringComparison.Ordinal));
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
        string workflowName, string repoFallbackUrl,
        IReadOnlyList<RunWithJobs> runsNewestFirst, IReadOnlyList<string> patterns)
    {
        var order = new List<string>();                 // canonical target keys, first-seen order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // O(1) dedup
        var signals = new Dictionary<string, JobSignal>(StringComparer.OrdinalIgnoreCase);

        foreach (var rwj in runsNewestFirst)
        {
            foreach (var job in rwj.Jobs.Where(j => !string.IsNullOrWhiteSpace(j.Name) && IsJobMatch(j.Name!, patterns)))
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
        GitHubJobDto job, GitHubRunSummary run, string workflowName, string repoFallbackUrl,
        List<string> order, HashSet<string> seen, Dictionary<string, JobSignal> signals)
    {
        var key = CanonicalJobTarget(job.Name!);
        if (seen.Add(key))
        {
            order.Add(key);  // HashSet.Add returns true on first insert
        }

        var state = ToSignalState(job.Status, job.Conclusion);
        if (state == SignalState.Unknown || signals.ContainsKey(key))
        {
            return;
        }

        signals[key] = new JobSignal(
            workflowName, job.Name!, state,
            JobUrl(job, run, repoFallbackUrl),
            job.CompletedAt ?? job.StartedAt ?? Instant.MinValue);
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
        IReadOnlyList<RunWithJobs> runsNewestFirst, List<string> order, Dictionary<string, JobSignal> signals)
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
                ? SignalState.Running : SignalState.Unknown;
        }
        return conclusion switch
        {
            "success" => SignalState.Success,
            "failure" or "timed_out" or "startup_failure" => SignalState.Failure,
            _ => SignalState.Unknown
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

        var isCodeQl = path.Contains("github-code-scanning", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CodeQL", StringComparison.OrdinalIgnoreCase);
        if (isCodeQl && !options.IncludeCodeQl)
        {
            return false;
        }

        return true;
    }

    public static string FileName(string path) => Path.GetFileName(path);

    // affectsAuthState gates whether this request drives the global auth-error
    // health signal. Primary endpoints (repos/workflows/runs) set it on a 401/403
    // and clear it on success, so /api/health reports a genuinely broken token.
    // Best-effort PR endpoints pass false: a token missing only the
    // "Pull requests: Read" scope 403s there, and treating that as a global auth
    // error flipped /api/health to Degraded until the next primary 200 cleared it
    // — a flap. Such a request still throws GitHubAuthException (its caller swallows
    // it and shows no PRs); it just never touches the shared health state.
    private async Task<T?> SendAsync<T>(string url, CancellationToken ct, bool affectsAuthState = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "fixportal-ci-backend");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Authorization = new("Bearer", _gitHub.Token);

        // Conditional GET: a matching validator yields 304 Not Modified, which GitHub
        // serves without charging the primary rate limit. We then return the payload
        // decoded from the prior 200 rather than re-fetching it.
        var cached = _etags.Get(url);
        if (cached is not null)
        {
            request.Headers.IfNoneMatch.Add(cached.ETag);
        }

        using var response = await httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            if (affectsAuthState)
            {
                state?.SetAuthError(null);
            }
            return (T?)cached.Payload;
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (affectsAuthState)
            {
                state?.SetAuthError(null);
            }
            return default;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var err = $"GitHub authentication failed (HTTP 401 Unauthorized) for {url}. Verify the configured PAT token.";
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

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && IsRateLimited(response))
        {
            throw new GitHubRateLimitException($"GitHub rate limit reached (HTTP {(int)response.StatusCode}) for {url}.");
        }
        _ = response.EnsureSuccessStatusCode();

        var value = await response.Content.ReadFromJsonAsync<T>(SerializerOptions, ct);

        // Remember the validator + payload so the next cycle can revalidate cheaply.
        if (response.Headers.ETag is { } etag)
        {
            _etags.Set(url, etag, value);
        }

        if (affectsAuthState)
        {
            state?.SetAuthError(null);
        }
        return value;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault()?.Trim(), out var left) && left == 0)
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
