# PR Review Status Pills (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish per-reviewer review state for each open pull request in the dashboard snapshot, without loading the 20-second board cycle or the GitHub rate budget.

**Architecture:** A new slow-cadence `ReviewSignalEnrichmentWorker` (subclassing the existing `RepoEnrichmentWorker<T>`) batches one GraphQL query plus one code-scanning REST call per repo, derives a per-reviewer state through a pure factory, and writes the result to a `PerRepoCache`. `DashboardRefreshService` merges the cached values onto the pull requests it already fetches. The whole feature is inert until reviewers are configured.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, System.Text.Json, NodaTime, xUnit v3 + AwesomeAssertions + NSubstitute, Stryker.

**Spec:** `fixportal-ci-frontend/docs/superpowers/specs/2026-07-31-pr-review-status-pills-design.md`

**Companion plan:** the frontend half lives in `fixportal-ci-frontend` at `docs/superpowers/plans/2026-07-31-pr-review-status-pills-frontend.md`. Ship this one first — it adds an optional field that nothing populates until configured, so it is safe to deploy ahead of any UI.

## Global Constraints

- The 20-second board refresh (`Dashboard:RefreshSeconds`) must never wait on, or fail because of, this feature. All new work happens on the enrichment worker.
- Ships **off**: `ReviewSignals:Reviewers` is empty in `appsettings.json`. With no reviewers configured, zero new HTTP requests may be issued.
- Never bind a non-empty default into a configuration collection. The binder *appends* to a pre-populated list rather than replacing it — this is why `DashboardOptions.JobLanes` defaults to `[]` with its defaults held separately in `DefaultJobLanes`.
- New GitHub calls pass `affectsAuthState: false`. An optional feature must never flip `/api/health` to Degraded.
- Assert with `.Should()` (AwesomeAssertions), never `Assert.*`. Follow the existing `_ = x.Should()...` discard style. Prefer one parameterised `[Theory]` over near-duplicate `[Fact]`s.
- REST responses deserialize with `JsonNamingPolicy.SnakeCaseLower`; GraphQL responses are camelCase and need their own options object. Mixing them silently yields all-null properties.
- `ReviewSignalState` is an enum. `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` is already registered at `Program.cs:20` and in `FileDashboardSnapshotStore.cs:18`, so it serializes as `"clean"` / `"outstanding"` with no extra work.
- No emoji in source, comments, or commit messages.
- Commit after each task. One push at the end.

---

### Task 1: Contract and configuration

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Dashboard/Model/DashboardModels.cs:32-39`
- Create: `src/FixPortal.Ci.Backend.Api/Dashboard/Configuration/ReviewSignalsOptions.cs`
- Modify: `src/FixPortal.Ci.Backend.Api/Program.cs` (options registration, near the other `Configure` calls)
- Modify: `src/FixPortal.Ci.Backend.Api/appsettings.json`
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalContractTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ReviewSignalState` (`Clean`, `Outstanding`, `Pending`, `Disabled`), `ReviewSignal(string Name, ReviewSignalState State, int? Count, string? HtmlUrl)`, `PullRequest.ReviewSignals` (optional trailing parameter), `ReviewSignalsOptions`, `ReviewerOptions`, `ReviewerSource`.

- [ ] **Step 1: Write the failing test**

Create `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalContractTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using NodaTime;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalContractTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static PullRequest Pr(IReadOnlyList<ReviewSignal>? signals = null) =>
        new(7, "Add widget", "alice", "https://github.com/FixPortal/repo/pull/7", false, Instant.FromUnixTimeSeconds(1000), signals);

    [Fact]
    public void Pull_request_defaults_to_no_review_signals()
    {
        _ = Pr().ReviewSignals.Should().BeNull();
    }

    [Fact]
    public void Review_signal_state_serializes_as_a_camel_case_string()
    {
        var json = JsonSerializer.Serialize(new ReviewSignal("CodeQL", ReviewSignalState.Outstanding, 2, null), Options);
        _ = json.Should().Contain("\"outstanding\"").And.Contain("\"count\":2");
    }

    [Fact]
    public void Absent_signals_are_omitted_from_the_wire_rather_than_sent_as_an_empty_array()
    {
        var json = JsonSerializer.Serialize(Pr(), Options);
        _ = json.Should().NotContain("reviewSignals");
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalContractTests`
Expected: FAIL — `ReviewSignal` and `ReviewSignalState` do not exist.

- [ ] **Step 3: Add the model**

In `src/FixPortal.Ci.Backend.Api/Dashboard/Model/DashboardModels.cs`, add above the existing `PullRequest` record:

```csharp
public enum ReviewSignalState
{
    /// <summary>The reviewer demonstrably ran and left nothing outstanding.</summary>
    Clean,

    /// <summary>The reviewer has items still open; Count carries how many.</summary>
    Outstanding,

    /// <summary>Required here, but no evidence it has run. Never render as clean.</summary>
    Pending,

    /// <summary>Not required on this pull request.</summary>
    Disabled,
}

public sealed record ReviewSignal(string Name, ReviewSignalState State, int? Count, string? HtmlUrl);
```

Then extend `PullRequest` with one optional trailing parameter, leaving the existing six untouched:

```csharp
public sealed record PullRequest(
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    bool IsDraft,
    Instant CreatedAt,
    // Optional and trailing so every existing construction site keeps compiling and
    // an older frontend simply never sees the field. Null means "nothing to show":
    // enrichment has not run, the author is excluded, or no reviewers are configured.
    IReadOnlyList<ReviewSignal>? ReviewSignals = null
);
```

- [ ] **Step 4: Add the options**

Create `src/FixPortal.Ci.Backend.Api/Dashboard/Configuration/ReviewSignalsOptions.cs`:

```csharp
namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

/// <summary>Where a reviewer's state is read from.</summary>
public enum ReviewerSource
{
    /// <summary>Unresolved review threads whose first comment is authored by BotLogin.</summary>
    ReviewThreads,

    /// <summary>Open code-scanning alerts on the pull request's head ref.</summary>
    CodeScanning,
}

public sealed class ReviewerOptions
{
    /// <summary>Display label shown on the pill, e.g. "CodeRabbit".</summary>
    public required string Name { get; init; }

    /// <summary>GitHub login of the reviewing bot. Required for ReviewThreads reviewers.</summary>
    public string? BotLogin { get; init; }

    /// <summary>
    /// When set, this reviewer is Disabled on any pull request that does not carry the
    /// label. Absent means the reviewer applies to every pull request.
    /// </summary>
    public string? RequiredLabel { get; init; }

    public ReviewerSource Source { get; init; } = ReviewerSource.ReviewThreads;
}

public sealed class ReviewSignalsOptions
{
    public bool Enabled { get; init; } = true;
    public int RefreshSeconds { get; init; } = 150;

    /// <summary>
    /// Pull request authors whose PRs get no review signals at all — dependency bots,
    /// which are out of AI code review by policy. Matched case-insensitively.
    /// </summary>
    public IReadOnlyList<string> ExcludedAuthors { get; init; } = [];

    /// <summary>
    /// The reviewers to report. EMPTY BY DEFAULT and deliberately so: the configuration
    /// binder APPENDS bound collection items to a pre-populated list rather than
    /// replacing it, so a compiled-in default would shadow every configured entry (the
    /// same trap documented on DashboardOptions.JobLanes). Empty also means the whole
    /// feature is off, and the worker issues no requests.
    /// </summary>
    public IReadOnlyList<ReviewerOptions> Reviewers { get; init; } = [];
}
```

- [ ] **Step 5: Register the options and add the config block**

In `src/FixPortal.Ci.Backend.Api/Program.cs`, beside the existing options registrations:

```csharp
builder.Services.Configure<ReviewSignalsOptions>(builder.Configuration.GetSection("ReviewSignals"));
```

In `src/FixPortal.Ci.Backend.Api/appsettings.json`, add a top-level `ReviewSignals` section after `Dashboard`. Shipped off — `Reviewers` stays empty here, and the live values go in deployment configuration:

```json
  "ReviewSignals": {
    "Enabled": true,
    "RefreshSeconds": 150,
    "ExcludedAuthors": [ "dependabot[bot]", "renovate[bot]" ],
    "Reviewers": []
  }
```

- [ ] **Step 6: Run the test and verify it passes**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalContractTests`
Expected: PASS, 3 tests.

- [ ] **Step 7: Build the whole solution to prove the record change is source-compatible**

Run: `dotnet build`
Expected: exit 0. Every existing `new PullRequest(...)` site still compiles because the new parameter is optional and trailing.

- [ ] **Step 8: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Dashboard/Model/DashboardModels.cs src/FixPortal.Ci.Backend.Api/Dashboard/Configuration/ReviewSignalsOptions.cs src/FixPortal.Ci.Backend.Api/Program.cs src/FixPortal.Ci.Backend.Api/appsettings.json tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalContractTests.cs
git commit -m "feat(model): add review signal contract and configuration"
```

---

### Task 2: GraphQL transport and review facts

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs:539-612` (extract two helpers, add the POST path and the public fetch)
- Create: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubReviewFacts.cs`
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs`

**Interfaces:**
- Consumes: `ReviewSignalsOptions` is not needed here; this task is pure transport plus mapping.
- Produces:
  - `PrReviewFacts(int Number, string AuthorLogin, IReadOnlySet<string> Labels, IReadOnlyDictionary<string, int> UnresolvedThreadsByAuthor, IReadOnlySet<string> ParticipatingAuthors, IReadOnlySet<string> SuccessfulCheckAppSlugs)`
  - `GitHubOrgClient.ToReviewFacts(ReviewFactsPull pull)` — static, unit-testable without HTTP.
  - `GitHubOrgClient.GetPullRequestReviewFactsAsync(string repo, CancellationToken ct)` returning `IReadOnlyDictionary<int, PrReviewFacts>`.

- [ ] **Step 1: Write the failing test**

Create `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs`. These exercise the pure mapper, in the style of `GitHubPullRequestTests`:

```csharp
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubReviewFactsTests
{
    private static ReviewFactsPull Pull(
        IReadOnlyList<GraphQlThread>? threads = null,
        IReadOnlyList<GraphQlReview>? reviews = null,
        IReadOnlyList<GraphQlLabel>? labels = null,
        IReadOnlyList<GraphQlContext>? checks = null
    ) =>
        new(
            181,
            new GraphQlActor("chris"),
            new NodeList<GraphQlLabel>(labels ?? []),
            new NodeList<GraphQlReview>(reviews ?? []),
            new NodeList<GraphQlThread>(threads ?? []),
            new NodeList<GraphQlCommitNode>(
                [new GraphQlCommitNode(new GraphQlCommit(new GraphQlRollup(new NodeList<GraphQlContext>(checks ?? []))))]
            )
        );

    private static GraphQlThread Thread(string author, bool resolved) =>
        new(resolved, new NodeList<GraphQlComment>([new GraphQlComment(new GraphQlActor(author))]));

    [Fact]
    public void Counts_only_unresolved_threads_and_keys_them_by_the_first_comment_author()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(threads: [Thread("coderabbitai", false), Thread("coderabbitai", false), Thread("coderabbitai", true), Thread("chris", false)])
        );

        _ = facts.UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(2);
        _ = facts.UnresolvedThreadsByAuthor["chris"].Should().Be(1);
    }

    [Fact]
    public void Treats_a_resolved_thread_as_participation_so_a_reviewer_that_ran_clean_is_not_pending()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(threads: [Thread("gitar-app", true)]));

        _ = facts.UnresolvedThreadsByAuthor.Should().NotContainKey("gitar-app");
        _ = facts.ParticipatingAuthors.Should().Contain("gitar-app");
    }

    [Fact]
    public void Records_a_review_author_as_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(reviews: [new GraphQlReview(new GraphQlActor("gitar-app"))]));

        _ = facts.ParticipatingAuthors.Should().Contain("gitar-app");
    }

    [Fact]
    public void Records_labels_and_the_author_login()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(labels: [new GraphQlLabel("review-high")]));

        _ = facts.Number.Should().Be(181);
        _ = facts.AuthorLogin.Should().Be("chris");
        _ = facts.Labels.Should().Contain("review-high");
    }

    [Fact]
    public void Records_only_successful_check_app_slugs()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(
                checks:
                [
                    new GraphQlContext("CodeQL", "SUCCESS", new GraphQlCheckSuite(new GraphQlApp("github-code-scanning"))),
                    new GraphQlContext("flaky", "FAILURE", new GraphQlCheckSuite(new GraphQlApp("some-app"))),
                ]
            )
        );

        _ = facts.SuccessfulCheckAppSlugs.Should().Contain("github-code-scanning");
        _ = facts.SuccessfulCheckAppSlugs.Should().NotContain("some-app");
    }

    [Fact]
    public void Survives_a_payload_with_null_collections_and_a_null_author()
    {
        var pull = new ReviewFactsPull(9, null, null, null, null, null);

        var facts = GitHubOrgClient.ToReviewFacts(pull);

        _ = facts.AuthorLogin.Should().Be("unknown");
        _ = facts.Labels.Should().BeEmpty();
        _ = facts.UnresolvedThreadsByAuthor.Should().BeEmpty();
    }

    [Fact]
    public void Matches_logins_case_insensitively_so_config_casing_cannot_silently_miss()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(threads: [Thread("CodeRabbitAI", false)]));

        _ = facts.UnresolvedThreadsByAuthor.ContainsKey("coderabbitai").Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~GitHubReviewFactsTests`
Expected: FAIL — none of the GraphQL types exist.

- [ ] **Step 3: Add the GraphQL payload types and the facts record**

Create `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubReviewFacts.cs`:

```csharp
namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

// GraphQL wire shapes. Deserialized with a CAMEL-CASE serializer, unlike every REST
// DTO in this project (which uses SnakeCaseLower) — GitHub's GraphQL API returns
// camelCase field names. Using the REST options object here yields all-null
// properties and no error, so the two must not be mixed.
public sealed record GraphQlEnvelope<T>(T? Data, IReadOnlyList<GraphQlError>? Errors);

public sealed record GraphQlError(string? Message);

public sealed record NodeList<T>(IReadOnlyList<T>? Nodes);

public sealed record GraphQlActor(string? Login);

public sealed record GraphQlLabel(string? Name);

public sealed record GraphQlReview(GraphQlActor? Author);

public sealed record GraphQlComment(GraphQlActor? Author);

public sealed record GraphQlThread(bool IsResolved, NodeList<GraphQlComment>? Comments);

public sealed record GraphQlApp(string? Slug);

public sealed record GraphQlCheckSuite(GraphQlApp? App);

public sealed record GraphQlContext(string? Name, string? Conclusion, GraphQlCheckSuite? CheckSuite);

public sealed record GraphQlRollup(NodeList<GraphQlContext>? Contexts);

public sealed record GraphQlCommit(GraphQlRollup? StatusCheckRollup);

public sealed record GraphQlCommitNode(GraphQlCommit? Commit);

public sealed record ReviewFactsPull(
    int Number,
    GraphQlActor? Author,
    NodeList<GraphQlLabel>? Labels,
    NodeList<GraphQlReview>? Reviews,
    NodeList<GraphQlThread>? ReviewThreads,
    NodeList<GraphQlCommitNode>? Commits
);

public sealed record ReviewFactsRepository(NodeList<ReviewFactsPull>? PullRequests);

public sealed record ReviewFactsData(ReviewFactsRepository? Repository);

/// <summary>
/// Everything needed to decide one pull request's reviewer states, flattened out of
/// the GraphQL payload. Deliberately reviewer-agnostic: it records who did what, and
/// the factory decides what that means for a configured reviewer.
/// </summary>
public sealed record PrReviewFacts(
    int Number,
    string AuthorLogin,
    IReadOnlySet<string> Labels,
    IReadOnlyDictionary<string, int> UnresolvedThreadsByAuthor,
    IReadOnlySet<string> ParticipatingAuthors,
    IReadOnlySet<string> SuccessfulCheckAppSlugs
);
```

- [ ] **Step 4: Add the mapper**

In `GitHubOrgClient.cs`, beside the other static mappers (near `ToPullRequest`):

```csharp
    /// <summary>
    /// Flattens one GraphQL pull-request node into the facts the signal factory needs.
    /// Every collection is treated as optional: GraphQL omits or nulls empty
    /// connections, and a missing author is legitimate for a deleted account.
    /// All string sets are case-insensitive so a configured BotLogin cannot miss on
    /// casing alone (GitHub logins are case-preserving but not case-sensitive).
    /// </summary>
    public static PrReviewFacts ToReviewFacts(ReviewFactsPull pull)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in pull.Labels?.Nodes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(label.Name))
            {
                _ = labels.Add(label.Name);
            }
        }

        var participating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in pull.Reviews?.Nodes ?? [])
        {
            if (review.Author?.Login is { Length: > 0 } login)
            {
                _ = participating.Add(login);
            }
        }

        var unresolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var thread in pull.ReviewThreads?.Nodes ?? [])
        {
            // The FIRST comment's author owns the thread. A human replying to a bot's
            // finding must not re-attribute that thread to the human.
            var author = thread.Comments?.Nodes is { Count: > 0 } comments ? comments[0].Author?.Login : null;
            if (author is not { Length: > 0 })
            {
                continue;
            }
            // A resolved thread still proves the reviewer ran — that is what separates
            // "clean" from "pending".
            _ = participating.Add(author);
            if (!thread.IsResolved)
            {
                unresolved[author] = unresolved.GetValueOrDefault(author) + 1;
            }
        }

        var checkApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in pull.Commits?.Nodes ?? [])
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

        return new PrReviewFacts(
            pull.Number,
            pull.Author?.Login is { Length: > 0 } author ? author : "unknown",
            labels,
            unresolved,
            participating,
            checkApps
        );
    }
```

- [ ] **Step 5: Run the mapper tests and verify they pass**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~GitHubReviewFactsTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit the mapper before touching transport**

```bash
git add src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubReviewFacts.cs src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs
git commit -m "feat(github): map GraphQL review payloads to per-PR facts"
```

- [ ] **Step 7: Extract the shared request helpers (pure refactor)**

In `GitHubOrgClient.cs`, split two blocks out of the existing `SendAsync` without changing behaviour. The header block at lines 542-545 becomes:

```csharp
    private void AddStandardHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "fixportal-ci-backend");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Authorization = new("Bearer", _gitHub.Token);
    }
```

The 401 / 403 / 429 mapping at lines 567-600 becomes (comments move with it verbatim):

```csharp
    private void GuardResponse(HttpResponseMessage response, string url, bool affectsAuthState)
    {
        // ...the existing Unauthorized, Forbidden-and-not-rate-limited, and
        // TooManyRequests-or-rate-limited blocks, moved unchanged...
    }
```

`SendAsync` then calls `AddStandardHeaders(request)` in place of the four header lines and `GuardResponse(response, url, affectsAuthState)` in place of the moved blocks. The 304 and 404 short-circuits stay in `SendAsync` — they are GET-specific.

- [ ] **Step 8: Prove the refactor changed nothing**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter "FullyQualifiedName~GitHubETagCachingTests|FullyQualifiedName~GitHubAuthorizationTests"`
Expected: PASS, unchanged. These suites drive the private `SendAsync` through public methods and are the regression net for this extraction.

- [ ] **Step 9: Add the GraphQL POST path and the public fetch**

In `GitHubOrgClient.cs`, beside `SerializerOptions`:

```csharp
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
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: 50, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes {
                number
                author { login }
                labels(first: 20) { nodes { name } }
                reviews(first: 50) { nodes { author { login } } }
                reviewThreads(first: 100) {
                  nodes { isResolved comments(first: 1) { nodes { author { login } } } }
                }
                commits(last: 1) {
                  nodes {
                    commit {
                      statusCheckRollup {
                        contexts(first: 50) {
                          nodes { ... on CheckRun { name conclusion checkSuite { app { slug } } } }
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
    /// One query per repo covering every open pull request. Batched deliberately: a
    /// per-PR query would multiply the request count by the open-PR total and break
    /// the rate budget. affectsAuthState is false throughout — this is a supplementary
    /// signal and a token missing a scope here must not flip /api/health.
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

        var facts = new Dictionary<int, PrReviewFacts>();
        foreach (var pull in data?.Repository?.PullRequests?.Nodes ?? [])
        {
            facts[pull.Number] = ToReviewFacts(pull);
        }
        return facts;
    }

    private async Task<T?> PostGraphQlAsync<T>(string query, object variables, string subject, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new { query, variables }, options: GraphQlSerializerOptions),
        };
        AddStandardHeaders(request);

        using var response = await httpClient.SendAsync(request, ct);
        GuardResponse(response, "graphql", affectsAuthState: false);
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
```

- [ ] **Step 10: Write the transport tests**

Append to `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs` a second class using the `ScriptedHandler` pattern copied from `GitHubETagCachingTests.cs:24-53`:

```csharp
public class GitHubReviewFactsTransportTests
{
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses.Dequeue());
        }
    }

    private static GitHubOrgClient CreateClient(HttpClient http) =>
        new(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 }),
            new GitHubETagStore()
        );

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Posts_to_graphql_and_parses_camel_case_field_names()
    {
        const string body = """
            {"data":{"repository":{"pullRequests":{"nodes":[
              {"number":181,"author":{"login":"chris"},
               "labels":{"nodes":[{"name":"review-high"}]},
               "reviews":{"nodes":[]},
               "reviewThreads":{"nodes":[{"isResolved":false,"comments":{"nodes":[{"author":{"login":"coderabbitai"}}]}}]},
               "commits":{"nodes":[]}}
            ]}}}}
            """;
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>([Json(body)]));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        var facts = await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        _ = handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/graphql");
        _ = facts[181].Labels.Should().Contain("review-high");
        _ = facts[181].UnresolvedThreadsByAuthor["coderabbitai"].Should().Be(1);
    }

    [Fact]
    public async Task Throws_when_graphql_reports_errors_in_a_200_response()
    {
        var handler = new ScriptedHandler(
            new Queue<HttpResponseMessage>([Json("""{"data":null,"errors":[{"message":"Could not resolve to a Repository"}]}""")])
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };

        var act = async () => await CreateClient(http).GetPullRequestReviewFactsAsync("repo", CancellationToken.None);

        _ = await act.Should().ThrowAsync<HttpRequestException>();
    }
}
```

Add the `using` directives the class needs: `System.Net`, `System.Text`, `FixPortal.Ci.Backend.Api.Dashboard.Configuration`, `Microsoft.Extensions.Options`.

- [ ] **Step 11: Run the transport tests and verify they pass**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~GitHubReviewFacts`
Expected: PASS, 9 tests total across both classes.

- [ ] **Step 12: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs
git commit -m "feat(github): fetch per-PR review facts over GraphQL"
```

---

### Task 3: Code-scanning alerts

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs` (new DTOs beside the existing ones, new public method)
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubCodeScanningTests.cs`

**Interfaces:**
- Consumes: `SendAsync<T>` from Task 2's refactor.
- Produces:
  - `GitHubOrgClient.PullNumberFromRef(string? gitRef)` returning `int?`.
  - `GitHubOrgClient.GetOpenCodeScanningAlertCountsAsync(string repo, CancellationToken ct)` returning `IReadOnlyDictionary<int, int>?` — **null means unavailable** (no permission, or scanning not enabled), which the factory renders as `Pending`. An empty dictionary means available with no open alerts.

- [ ] **Step 1: Write the failing test**

Create `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubCodeScanningTests.cs`:

```csharp
using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubCodeScanningTests
{
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private static GitHubOrgClient CreateClient(HttpClient http) =>
        new(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 }),
            new GitHubETagStore()
        );

    private static HttpClient Responding(HttpStatusCode status, string body = "[]")
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return new HttpClient(new ScriptedHandler(new Queue<HttpResponseMessage>([response])))
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
    }

    [Theory]
    [InlineData("refs/pull/181/head", 181)]
    [InlineData("refs/pull/7/merge", 7)]
    [InlineData("refs/heads/main", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("refs/pull/notanumber/head", null)]
    public void PullNumberFromRef_extracts_only_pull_request_refs(string? gitRef, int? expected)
    {
        _ = GitHubOrgClient.PullNumberFromRef(gitRef).Should().Be(expected);
    }

    [Fact]
    public async Task Buckets_open_alerts_by_pull_request_number()
    {
        const string body = """
            [
              {"most_recent_instance":{"ref":"refs/pull/181/head"}},
              {"most_recent_instance":{"ref":"refs/pull/181/head"}},
              {"most_recent_instance":{"ref":"refs/pull/179/head"}},
              {"most_recent_instance":{"ref":"refs/heads/main"}}
            ]
            """;
        using var http = Responding(HttpStatusCode.OK, body);

        var counts = await CreateClient(http).GetOpenCodeScanningAlertCountsAsync("repo", CancellationToken.None);

        _ = counts.Should().NotBeNull();
        _ = counts![181].Should().Be(2);
        _ = counts[179].Should().Be(1);
        _ = counts.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Reports_unavailable_rather_than_zero_when_scanning_cannot_be_read(HttpStatusCode status)
    {
        using var http = Responding(status);

        var counts = await CreateClient(http).GetOpenCodeScanningAlertCountsAsync("repo", CancellationToken.None);

        // Null, not empty: an empty dictionary would render the CodeQL pill green,
        // claiming a clean scan that never ran.
        _ = counts.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~GitHubCodeScanningTests`
Expected: FAIL — `PullNumberFromRef` and `GetOpenCodeScanningAlertCountsAsync` do not exist.

- [ ] **Step 3: Write the implementation**

In `GitHubOrgClient.cs`, add the DTOs beside the existing REST DTOs (these deserialize with the snake_case `SerializerOptions`, so `most_recent_instance` binds without annotation):

```csharp
public sealed record CodeScanningInstanceDto(string? Ref);

public sealed record CodeScanningAlertDto(CodeScanningInstanceDto? MostRecentInstance);
```

Then the parser and the fetch:

```csharp
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
        List<CodeScanningAlertDto>? alerts;
        try
        {
            // affectsAuthState: false — a missing code-scanning scope 403s here and must
            // not flip /api/health, exactly as the pull-request listing does.
            alerts = await SendAsync<List<CodeScanningAlertDto>>(
                $"repos/{_gitHub.Owner}/{repo}/code-scanning/alerts?state=open&per_page=100",
                ct,
                affectsAuthState: false
            );
        }
        catch (GitHubAuthException)
        {
            return null;
        }

        // SendAsync maps 404 to default(T) — repo has scanning disabled entirely.
        if (alerts is null)
        {
            return null;
        }

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
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~GitHubCodeScanningTests`
Expected: PASS, 9 tests (6 theory cases plus 3).

- [ ] **Step 5: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubCodeScanningTests.cs
git commit -m "feat(github): count open code-scanning alerts per pull request"
```

---

### Task 4: The derivation factory

**Files:**
- Create: `src/FixPortal.Ci.Backend.Api/Dashboard/Services/ReviewSignalFactory.cs`
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs`

**Interfaces:**
- Consumes: `PrReviewFacts` (Task 2), `ReviewerOptions` / `ReviewerSource` (Task 1), `ReviewSignal` / `ReviewSignalState` (Task 1).
- Produces: `ReviewSignalFactory.Build(PrReviewFacts facts, IReadOnlyList<ReviewerOptions> reviewers, int? openAlerts, string prHtmlUrl)` returning `IReadOnlyList<ReviewSignal>`.

- [ ] **Step 1: Write the failing test**

Create `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs`:

```csharp
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Dashboard;

public class ReviewSignalFactoryTests
{
    private const string PrUrl = "https://github.com/FixPortal/repo/pull/181";

    private static readonly ReviewerOptions CodeRabbit = new()
    {
        Name = "CodeRabbit",
        BotLogin = "coderabbitai",
        RequiredLabel = "review-high",
    };

    private static readonly ReviewerOptions Gitar = new() { Name = "Gitar", BotLogin = "gitar-app" };

    private static readonly ReviewerOptions CodeQl = new() { Name = "CodeQL", Source = ReviewerSource.CodeScanning };

    private static PrReviewFacts Facts(
        IEnumerable<string>? labels = null,
        IDictionary<string, int>? unresolved = null,
        IEnumerable<string>? participating = null,
        IEnumerable<string>? checkApps = null
    ) =>
        new(
            181,
            "chris",
            new HashSet<string>(labels ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(unresolved ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(participating ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(checkApps ?? [], StringComparer.OrdinalIgnoreCase)
        );

    private static ReviewSignal Only(ReviewerOptions reviewer, PrReviewFacts facts, int? openAlerts = null) =>
        ReviewSignalFactory.Build(facts, [reviewer], openAlerts, PrUrl)[0];

    [Fact]
    public void Disabled_when_the_required_label_is_absent()
    {
        _ = Only(CodeRabbit, Facts()).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Disabled_outranks_outstanding_so_an_unrequired_reviewer_never_shows_red()
    {
        var facts = Facts(unresolved: new Dictionary<string, int> { ["coderabbitai"] = 4 });

        _ = Only(CodeRabbit, facts).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Outstanding_with_a_count_when_the_bot_has_unresolved_threads()
    {
        var facts = Facts(labels: ["review-high"], unresolved: new Dictionary<string, int> { ["coderabbitai"] = 3 });

        var signal = Only(CodeRabbit, facts);

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.Count.Should().Be(3);
        _ = signal.HtmlUrl.Should().Be($"{PrUrl}/files");
    }

    [Fact]
    public void A_humans_unresolved_thread_is_not_the_bots_problem()
    {
        var facts = Facts(labels: ["review-high"], unresolved: new Dictionary<string, int> { ["chris"] = 2 });

        _ = Only(CodeRabbit, facts).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Clean_when_the_bot_participated_and_left_nothing_unresolved()
    {
        _ = Only(Gitar, Facts(participating: ["gitar-app"])).State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void Clean_when_the_only_evidence_is_a_successful_check_from_that_app()
    {
        _ = Only(Gitar, Facts(checkApps: ["gitar-app"])).State.Should().Be(ReviewSignalState.Clean);
    }

    [Fact]
    public void Pending_when_a_required_reviewer_is_simply_silent()
    {
        // The paused-Gitar case. Silence must never read as a pass.
        var signal = Only(Gitar, Facts());

        _ = signal.State.Should().Be(ReviewSignalState.Pending);
        _ = signal.HtmlUrl.Should().BeNull();
        _ = signal.Count.Should().BeNull();
    }

    [Theory]
    [InlineData(2, ReviewSignalState.Outstanding)]
    [InlineData(0, ReviewSignalState.Clean)]
    public void Code_scanning_state_follows_the_open_alert_count_when_a_scan_has_run(int alerts, ReviewSignalState expected)
    {
        var facts = Facts(checkApps: ["github-code-scanning"]);

        _ = Only(CodeQl, facts, alerts).State.Should().Be(expected);
    }

    [Fact]
    public void Code_scanning_is_pending_when_no_scan_has_run_even_with_zero_alerts()
    {
        _ = Only(CodeQl, Facts(), 0).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Code_scanning_is_pending_when_alerts_could_not_be_read()
    {
        // null openAlerts = endpoint unavailable. Must not render as a clean scan.
        _ = Only(CodeQl, Facts(checkApps: ["github-code-scanning"]), null).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Builds_one_signal_per_configured_reviewer_in_configuration_order()
    {
        var signals = ReviewSignalFactory.Build(Facts(), [CodeRabbit, Gitar, CodeQl], null, PrUrl);

        _ = signals.Select(s => s.Name).Should().Equal("CodeRabbit", "Gitar", "CodeQL");
    }

    [Fact]
    public void A_review_threads_reviewer_with_no_bot_login_is_pending_rather_than_falsely_clean()
    {
        var misconfigured = new ReviewerOptions { Name = "Mystery" };

        _ = Only(misconfigured, Facts(participating: ["someone"])).State.Should().Be(ReviewSignalState.Pending);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalFactoryTests`
Expected: FAIL — `ReviewSignalFactory` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/FixPortal.Ci.Backend.Api/Dashboard/Services/ReviewSignalFactory.cs`:

```csharp
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Turns one pull request's observed facts into a per-reviewer state. Pure and
/// reviewer-agnostic: the reviewers are configuration, so no FixPortal review policy
/// is compiled in.
///
/// The ordering below is the whole design. Disabled is decided first, so a reviewer
/// that does not apply here never shows red. Clean requires POSITIVE evidence that the
/// reviewer ran: without that rule, a paused reviewer and a reviewer that finished
/// cleanly both present as zero unresolved threads, and the board would report a pass
/// nobody performed.
/// </summary>
public static class ReviewSignalFactory
{
    // The GitHub App that publishes CodeQL and other code-scanning results.
    private const string CodeScanningAppSlug = "github-code-scanning";

    public static IReadOnlyList<ReviewSignal> Build(
        PrReviewFacts facts,
        IReadOnlyList<ReviewerOptions> reviewers,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        var signals = new List<ReviewSignal>(reviewers.Count);
        foreach (var reviewer in reviewers)
        {
            signals.Add(BuildOne(facts, reviewer, openAlerts, prHtmlUrl));
        }
        return signals;
    }

    private static ReviewSignal BuildOne(
        PrReviewFacts facts,
        ReviewerOptions reviewer,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        if (reviewer.RequiredLabel is { Length: > 0 } label && !facts.Labels.Contains(label))
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Disabled, null, null);
        }

        return reviewer.Source == ReviewerSource.CodeScanning
            ? BuildCodeScanning(facts, reviewer, openAlerts, prHtmlUrl)
            : BuildReviewThreads(facts, reviewer, prHtmlUrl);
    }

    private static ReviewSignal BuildCodeScanning(
        PrReviewFacts facts,
        ReviewerOptions reviewer,
        int? openAlerts,
        string prHtmlUrl
    )
    {
        // Unreadable alerts (no permission, scanning disabled) are unknown, not clean.
        if (openAlerts is not { } alerts)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }
        if (alerts > 0)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Outstanding, alerts, $"{prHtmlUrl}/checks");
        }
        // Zero alerts only means clean once a scan has actually completed for this PR.
        return facts.SuccessfulCheckAppSlugs.Contains(CodeScanningAppSlug)
            ? new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null)
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
    }

    private static ReviewSignal BuildReviewThreads(PrReviewFacts facts, ReviewerOptions reviewer, string prHtmlUrl)
    {
        // A reviewer with no configured login can never be matched. Report Pending
        // rather than Clean so a configuration mistake surfaces as "not reviewed"
        // instead of a green pill nobody earned.
        if (reviewer.BotLogin is not { Length: > 0 } login)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
        }

        var unresolved = facts.UnresolvedThreadsByAuthor.GetValueOrDefault(login);
        if (unresolved > 0)
        {
            return new ReviewSignal(reviewer.Name, ReviewSignalState.Outstanding, unresolved, $"{prHtmlUrl}/files");
        }

        var ran = facts.ParticipatingAuthors.Contains(login) || facts.SuccessfulCheckAppSlugs.Contains(login);
        return ran
            ? new ReviewSignal(reviewer.Name, ReviewSignalState.Clean, null, null)
            : new ReviewSignal(reviewer.Name, ReviewSignalState.Pending, null, null);
    }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalFactoryTests`
Expected: PASS, 14 tests (12 facts plus a 2-case theory).

- [ ] **Step 5: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Dashboard/Services/ReviewSignalFactory.cs tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs
git commit -m "feat(dashboard): derive per-reviewer signal states"
```

---

### Task 5: Worker, cache and snapshot merge

**Files:**
- Create: `src/FixPortal.Ci.Backend.Api/Dashboard/HostedServices/ReviewSignalEnrichmentWorker.cs`
- Modify: `src/FixPortal.Ci.Backend.Api/Dashboard/Services/DashboardRefreshService.cs:10-22` (constructor) and `:162-176` (merge site)
- Modify: `src/FixPortal.Ci.Backend.Api/Program.cs:108-132` (cache and hosted-service registration)
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalMergeTests.cs`

**Interfaces:**
- Consumes: `GetPullRequestReviewFactsAsync` and `GetOpenCodeScanningAlertCountsAsync` (Tasks 2-3), `ReviewSignalFactory.Build` (Task 4), `RepoEnrichmentWorker<T>`, `PerRepoCache<T>`.
- Produces: `DashboardRefreshService.ApplyReviewSignals(IReadOnlyList<PullRequest> prs, IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>? signals)` and a populated `PullRequest.ReviewSignals` in the snapshot.

- [ ] **Step 1: Write the failing test**

Create `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalMergeTests.cs`:

```csharp
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using NodaTime;
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
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalMergeTests`
Expected: FAIL — `ApplyReviewSignals` does not exist.

- [ ] **Step 3: Add the merge helper and wire it into the collect path**

In `DashboardRefreshService.cs`, add the static helper beside the existing `MergeWithPrevious`:

```csharp
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
```

Add the cache to the primary constructor, after `mergedPrs`:

```csharp
    PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>> reviewSignals,
```

And in `CollectRepoAsync`, replace the single `var pullRequests = await TryListOpenPullRequestsAsync(repo.Name, rateLimitToken);` line with:

```csharp
            var openPrs = await TryListOpenPullRequestsAsync(repo.Name, rateLimitToken);
            _ = reviewSignals.TryGet(repo.Name, out var repoReviewSignals);
            var pullRequests = ApplyReviewSignals(openPrs, repoReviewSignals);
```

- [ ] **Step 4: Run the merge tests and verify they pass**

Run: `dotnet test tests/FixPortal.Ci.Backend.Api.Tests --filter FullyQualifiedName~ReviewSignalMergeTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Write the worker**

Create `src/FixPortal.Ci.Backend.Api/Dashboard/HostedServices/ReviewSignalEnrichmentWorker.cs`:

```csharp
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.HostedServices;

/// <summary>
/// Slow-cadence enrichment (default 150s): fetches each repo's open-PR review state in
/// one batched GraphQL query plus one code-scanning call, and caches a per-PR signal
/// list. Off the 20s board loop deliberately — a per-PR fetch on that cadence would
/// exceed the PAT rate budget several times over. Disabled unless reviewers are
/// configured, so the default deployment issues no extra requests at all.
/// </summary>
public sealed class ReviewSignalEnrichmentWorker(
    GitHubOrgClient client,
    GitHubInventoryCache inventory,
    PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>> cache,
    IOptions<ReviewSignalsOptions> options,
    IOptions<GitHubOptions> gitHub,
    TimeProvider timeProvider,
    ILogger<ReviewSignalEnrichmentWorker> logger
) : RepoEnrichmentWorker<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>(
    client,
    inventory,
    cache,
    timeProvider,
    logger
)
{
    // No reviewers configured means the feature is off: the base class logs once and
    // the worker idles without issuing a single request.
    protected override bool Enabled => options.Value.Enabled && options.Value.Reviewers.Count > 0;

    protected override TimeSpan Cadence => TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    protected override string Name => "PR review signals";

    protected override async Task<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>?> CollectAsync(
        GitHubRepoDto repo,
        CancellationToken ct
    )
    {
        var reviewers = options.Value.Reviewers;
        try
        {
            var facts = await Client.GetPullRequestReviewFactsAsync(repo.Name, ct);
            if (facts.Count == 0)
            {
                return new Dictionary<int, IReadOnlyList<ReviewSignal>>();
            }

            // Only pay for the alerts call when a configured reviewer actually reads it.
            var needsAlerts = reviewers.Any(r => r.Source == ReviewerSource.CodeScanning);
            var alerts = needsAlerts ? await Client.GetOpenCodeScanningAlertCountsAsync(repo.Name, ct) : null;

            var signals = new Dictionary<int, IReadOnlyList<ReviewSignal>>();
            foreach (var pr in facts.Values)
            {
                // Dependency bots are out of AI code review by policy, so their PRs carry
                // no pills at all rather than a row of disabled ones.
                if (options.Value.ExcludedAuthors.Contains(pr.AuthorLogin, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                // A null alerts dictionary means "unreadable" and must stay null per PR;
                // a present dictionary with no entry for this PR means zero open alerts.
                var openAlerts = alerts is null ? (int?)null : alerts.GetValueOrDefault(pr.Number);
                signals[pr.Number] = ReviewSignalFactory.Build(
                    pr,
                    reviewers,
                    openAlerts,
                    $"https://github.com/{gitHub.Value.Owner}/{repo.Name}/pull/{pr.Number}"
                );
            }
            return signals;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or GitHubRateLimitException
                || ex is TaskCanceledException && !ct.IsCancellationRequested
            )
        {
            logger.LogWarning(ex, "Failed to fetch review signals for {Repo}; keeping last-known-good.", repo.Name);
            return null;
        }
    }
}
```

- [ ] **Step 6: Register the cache and the worker**

In `Program.cs`, beside the other `PerRepoCache` registrations (around line 110):

```csharp
builder.Services.AddSingleton<PerRepoCache<IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>>>>();
```

And beside the other hosted services (around line 132):

```csharp
builder.Services.AddHostedService<ReviewSignalEnrichmentWorker>();
```

- [ ] **Step 7: Write the off-by-default test**

Append to `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalMergeTests.cs` a class proving the feature is genuinely inert when unconfigured. "Off by default" is only true if nothing fires:

```csharp
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
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "t" }),
            Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 }),
            new GitHubETagStore()
        );
        var worker = new ReviewSignalEnrichmentWorker(
            client,
            new GitHubInventoryCache(client, Options.Create(new DashboardOptions { SnapshotPath = "x", RefreshSeconds = 20 })),
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
```

Add the `using` directives this class needs: `System.Net`, `FixPortal.Ci.Backend.Api.Dashboard.Configuration`, `FixPortal.Ci.Backend.Api.Dashboard.HostedServices`, `FixPortal.Ci.Backend.Api.Integrations.GitHub`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Time.Testing`. If `GitHubInventoryCache`'s constructor differs from the two arguments above, match its real signature rather than changing the cache.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: PASS. Every pre-existing test must still pass — the constructor change to `DashboardRefreshService` is the one place a compile break would surface.

- [ ] **Step 9: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Dashboard/HostedServices/ReviewSignalEnrichmentWorker.cs src/FixPortal.Ci.Backend.Api/Dashboard/Services/DashboardRefreshService.cs src/FixPortal.Ci.Backend.Api/Program.cs tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalMergeTests.cs
git commit -m "feat(dashboard): enrich open PRs with review signals"
```

---

### Task 6: Operator documentation and the push

**Files:**
- Modify: `operator-handoff.md:44-67` (PAT permissions section)
- Modify: `README.md` (snapshot shape / configuration section, wherever `Dashboard:` options are documented)

**Interfaces:**
- Consumes: everything above.
- Produces: a branch ready for one push.

- [ ] **Step 1: Document the new PAT permission**

In `operator-handoff.md`, add *Code scanning alerts: read* to the fine-grained PAT's repository permissions list, with a one-line note that without it the CodeQL signal reports "not yet reviewed" and no other signal is affected.

- [ ] **Step 2: Document the configuration**

In `README.md`, document the `ReviewSignals` section: `Enabled`, `RefreshSeconds`, `ExcludedAuthors`, and the `Reviewers` array with its `Name` / `BotLogin` / `RequiredLabel` / `Source` fields. State plainly that it ships with an empty `Reviewers` list and issues no requests until configured, and give the FixPortal values as the worked example.

- [ ] **Step 3: Full local gate**

Run: `dotnet build`
Expected: exit 0, no warnings introduced (the project treats analyzer findings seriously; fix rather than suppress).

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add operator-handoff.md README.md
git commit -m "docs: describe review signal configuration and PAT permission"
```

- [ ] **Step 5: Push once and open the PR**

```bash
pwsh -File C:/Users/chris/.claude/hooks/pr-gate-sentinel.ps1
git push -u origin feat/pr-review-signals
```

Then `gh pr create`, strip the injected emoji line from the body, request Gitar by hand (`gh pr comment <N> --body "Gitar review"`), and follow the tier the review-gate hook reports.

- [ ] **Step 6: Post-merge verification, before the frontend ships**

After deploying, confirm the field actually appears. Set the FixPortal `ReviewSignals:Reviewers` values in deployment configuration first, then:

```bash
curl -s https://<backend-host>/api/dashboard/snapshot | jq '.repositories[].pullRequests[] | select(.reviewSignals != null) | {number, reviewSignals}'
```

Expected: at least one pull request carrying a `reviewSignals` array with states as strings. If every signal is `pending`, check Gitar's `BotLogin` against a real PR's review author before assuming the derivation is wrong.

---

## Verification summary

| Spec requirement | Task |
|---|---|
| Optional `ReviewSignals` on `PullRequest`, backward compatible | 1 |
| Reviewers are configuration, no policy compiled in | 1, 4 |
| Ships off; zero requests when unconfigured | 1, 5 (step 7) |
| Binder-append trap avoided on the reviewer list | 1 |
| Per-repo GraphQL batch, not per PR | 2 |
| GraphQL camelCase vs REST snake_case kept separate | 2 |
| GraphQL 200-with-errors retains last-known-good | 2, 5 |
| Code-scanning alerts bucketed onto PR head refs | 3 |
| 403/404 renders pending, never clean | 3, 4 |
| `disabled` decided before every other state | 4 |
| `clean` requires positive evidence of a run | 4 |
| First-comment author owns a thread | 2 |
| Dependabot PRs carry no signals | 5 |
| 150s cadence, off the 20s board loop | 5 |
| `affectsAuthState: false` throughout | 2, 3 |
| Operator PAT permission documented | 6 |
