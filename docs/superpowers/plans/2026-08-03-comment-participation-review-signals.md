# Comment Participation for Review Signals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a review pill reach `Clean` for a reviewer that announces a clean result as a plain issue comment rather than a submitted review.

**Architecture:** An opt-in per-reviewer flag adds issue comments as a second channel of participation evidence, layered onto the existing review-thread logic rather than replacing it. Comments are head-scoped by timestamp (they carry no commit oid), all time parsing stays in the GraphQL mapper, and every failure path resolves to `Pending`.

**Tech Stack:** .NET, xunit.v3, AwesomeAssertions, NodaTime, GitHub GraphQL API, Bicep.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-03-comment-participation-review-signals-design.md`
- Assert with `.Should()` from `AwesomeAssertions`. Never `Assert.*`. Existing tests prefix assertions with `_ =` to satisfy the analyzer — match that.
- Wire records stay NodaTime-free: GraphQL timestamps are carried as raw `string?` and parsed to `Instant` in the mapper only.
- Every new failure path resolves to `ReviewSignalState.Pending`, never `Clean`.
- Comment head-scoping uses strict `>`. A comment timestamped equal to the head commit does not count.
- Truncation on the comments connection is detected with `HasPreviousPage`, not `HasNextPage` — it is fetched with `last:`, so overflow is at the opposite end from every other connection here.
- New record members are added as trailing positional parameters with defaults wherever that keeps existing construction sites compiling. `PrReviewFacts.HeadCommentAuthors` is the one deliberate exception — it takes no default, so every construction site must be updated.
- Run tests from the repo root: `dotnet test`.

---

### Task 1: Configuration flag and startup validation

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Dashboard/Configuration/ReviewSignalsOptions.cs`
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Configuration/ReviewSignalsConfigBindingTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ReviewerOptions.CommentsCountAsParticipation` (`bool`, defaults `false`), consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

In `ReviewSignalsConfigBindingTests.cs`, extend the existing `Reviewer` helper in `ReviewSignalsOptionsValidationTests` to take the new flag, then add two tests. Replace the `Reviewer` helper with:

```csharp
    private static Dictionary<string, string> Reviewer(
        string name,
        string? botLogin,
        string? source = null,
        bool? commentsCountAsParticipation = null
    )
    {
        var settings = new Dictionary<string, string> { ["ReviewSignals:Reviewers:0:Name"] = name };
        if (botLogin is not null)
        {
            settings["ReviewSignals:Reviewers:0:BotLogin"] = botLogin;
        }
        if (source is not null)
        {
            settings["ReviewSignals:Reviewers:0:Source"] = source;
        }
        if (commentsCountAsParticipation is { } flag)
        {
            settings["ReviewSignals:Reviewers:0:CommentsCountAsParticipation"] = flag ? "true" : "false";
        }
        return settings;
    }
```

Add these two tests to `ReviewSignalsOptionsValidationTests`:

```csharp
    [Fact]
    public void Comment_participation_binds_from_the_env_var_shape()
    {
        using var provider = Provider(
            Reviewer(name: "Gitar", botLogin: "gitar-bot", commentsCountAsParticipation: true)
        );

        var options = provider.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value;

        _ = options.Reviewers[0].CommentsCountAsParticipation.Should().BeTrue();
    }

    [Fact]
    public void Comment_participation_without_a_bot_login_is_rejected_at_startup()
    {
        // The flag matches comments BY BotLogin. Set without one it can never match, and
        // the reviewer would report Pending forever -- the same silent misconfiguration
        // the ReviewThreads BotLogin rule exists to prevent.
        using var provider = Provider(
            Reviewer(
                name: "Mystery",
                botLogin: null,
                source: nameof(ReviewerSource.CodeScanning),
                commentsCountAsParticipation: true
            )
        );

        _ = provider
            .Invoking(p => p.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value)
            .Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*CommentsCountAsParticipation*");
    }
```

Note the second test uses `Source=CodeScanning` deliberately: a `ReviewThreads` reviewer with no `BotLogin` is already rejected by the existing rule, so it would pass for the wrong reason and prove nothing.

Also add a defaults assertion to `Shipped_appsettings_configures_no_reviewers_so_the_feature_is_off`? No — that test asserts an empty reviewer list, so there is nothing to assert the flag against. Leave it unchanged.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ReviewSignalsOptionsValidationTests"`
Expected: compile error — `ReviewerOptions` has no `CommentsCountAsParticipation`.

- [ ] **Step 3: Add the property**

In `ReviewSignalsOptions.cs`, add to `ReviewerOptions` immediately after `RequiredLabel`:

```csharp
    /// <summary>
    /// When set, an issue comment from <see cref="BotLogin"/> dated after the head commit
    /// also counts as participation. For reviewers that report findings as review threads
    /// but announce a clean result as a plain comment: without this they hold Pending
    /// forever, because a comment is neither a review nor a thread.
    /// </summary>
    public bool CommentsCountAsParticipation { get; init; }
```

- [ ] **Step 4: Add the validation rule**

In `AddReviewSignalsOptions`, add after the existing `BotLogin` rule:

```csharp
            .Validate(
                o => o.Reviewers.All(r => !r.CommentsCountAsParticipation || !string.IsNullOrWhiteSpace(r.BotLogin)),
                "Every ReviewSignals:Reviewers entry with CommentsCountAsParticipation=true must set a non-blank BotLogin, or it can never match and reports Pending forever."
            )
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ReviewSignalsOptionsValidationTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Dashboard/Configuration/ReviewSignalsOptions.cs tests/FixPortal.Ci.Backend.Api.Tests/Configuration/ReviewSignalsConfigBindingTests.cs
git commit -m "feat(review-signals): add opt-in comment participation flag"
```

---

### Task 2: Wire shapes, facts, and the mapper

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubReviewFacts.cs`
- Modify: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs` (`ToReviewFacts` and collectors, around lines 713-810)
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `GraphQlIssueComment(GraphQlActor? Author, string? CreatedAt)`
  - `GraphQlCommit(string? Oid, GraphQlRollup? StatusCheckRollup, string? CommittedDate = null)`
  - `GraphQlPageInfo(bool HasNextPage, bool HasPreviousPage = false)`
  - `ReviewFactsPull(..., NodeList<GraphQlIssueComment>? Comments = null)` — trailing, so existing fixtures compile unchanged
  - `PrReviewFacts.HeadCommentAuthors` (`IReadOnlySet<string>`), positional slot 6 of 7, no default — consumed by Task 3

- [ ] **Step 1: Write the failing tests**

Add to `GitHubReviewFactsTests.cs`. First extend the `Pull` helper to carry comments and a head commit date — replace it with:

```csharp
    private const string HeadCommittedAt = "2026-08-03T10:00:00Z";

    private static ReviewFactsPull Pull(
        IReadOnlyList<GraphQlThread>? threads = null,
        IReadOnlyList<GraphQlReview>? reviews = null,
        IReadOnlyList<GraphQlLabel>? labels = null,
        IReadOnlyList<GraphQlContext>? checks = null,
        string? headOid = HeadOid,
        IReadOnlyList<GraphQlIssueComment>? comments = null,
        string? headCommittedAt = HeadCommittedAt
    ) =>
        new(
            181,
            new GraphQlActor("chris"),
            new NodeList<GraphQlLabel>(labels ?? []),
            new NodeList<GraphQlReview>(reviews ?? []),
            new NodeList<GraphQlThread>(threads ?? []),
            headOid is null
                ? new NodeList<GraphQlCommitNode>([])
                : new NodeList<GraphQlCommitNode>([
                    new GraphQlCommitNode(
                        new GraphQlCommit(
                            headOid,
                            new GraphQlRollup(new NodeList<GraphQlContext>(checks ?? [])),
                            headCommittedAt
                        )
                    ),
                ]),
            new NodeList<GraphQlIssueComment>(comments ?? [])
        );

    private static GraphQlIssueComment Comment(string author, string createdAt) =>
        new(new GraphQlActor(author), createdAt);
```

Then add these tests:

```csharp
    [Fact]
    public void A_comment_after_the_head_commit_is_head_comment_participation()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(comments: [Comment("gitar-bot", "2026-08-03T10:05:00Z")]));

        _ = facts.HeadCommentAuthors.Should().Contain("gitar-bot");
    }

    [Fact]
    public void A_comment_before_the_head_commit_is_stale_and_does_not_count()
    {
        // The bot commented, then the author pushed. That verdict is about old code.
        var facts = GitHubOrgClient.ToReviewFacts(Pull(comments: [Comment("gitar-bot", "2026-08-03T09:55:00Z")]));

        _ = facts.HeadCommentAuthors.Should().BeEmpty();
    }

    [Fact]
    public void A_comment_exactly_at_the_head_commit_does_not_count()
    {
        // Strict >. Equal timestamps are ambiguous, and Pending is the safe direction.
        var facts = GitHubOrgClient.ToReviewFacts(Pull(comments: [Comment("gitar-bot", HeadCommittedAt)]));

        _ = facts.HeadCommentAuthors.Should().BeEmpty();
    }

    [Fact]
    public void An_unparseable_comment_timestamp_is_skipped_rather_than_throwing()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(comments: [Comment("gitar-bot", "not-a-date"), Comment("github-code-quality", "2026-08-03T10:05:00Z")])
        );

        _ = facts.HeadCommentAuthors.Should().BeEquivalentTo("github-code-quality");
    }

    [Fact]
    public void No_head_commit_date_means_no_comment_can_be_head_scoped()
    {
        var facts = GitHubOrgClient.ToReviewFacts(
            Pull(comments: [Comment("gitar-bot", "2026-08-03T10:05:00Z")], headCommittedAt: null)
        );

        _ = facts.HeadCommentAuthors.Should().BeEmpty();
    }

    [Fact]
    public void Comment_authors_are_matched_case_insensitively_like_every_other_login_set()
    {
        var facts = GitHubOrgClient.ToReviewFacts(Pull(comments: [Comment("Gitar-Bot", "2026-08-03T10:05:00Z")]));

        _ = facts.HeadCommentAuthors.Should().Contain("gitar-bot");
    }

    [Fact]
    public void A_comment_never_leaks_into_head_review_participation()
    {
        // The two sets are distinct evidence channels. Only the configured flag joins them,
        // and that decision belongs to the factory, not the mapper.
        var facts = GitHubOrgClient.ToReviewFacts(Pull(comments: [Comment("gitar-bot", "2026-08-03T10:05:00Z")]));

        _ = facts.HeadParticipatingAuthors.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GitHubReviewFactsTests"`
Expected: compile errors — `GraphQlIssueComment` does not exist, `GraphQlCommit` takes 2 arguments, `ReviewFactsPull` takes 6, `PrReviewFacts` has no `HeadCommentAuthors`.

- [ ] **Step 3: Add the wire shapes**

In `GitHubReviewFacts.cs`:

Add after the `GraphQlComment` record:

```csharp
// An ISSUE comment on the pull request, distinct from GraphQlComment (a REVIEW comment,
// which anchors to a commit). Issue comments carry no commit reference at all, so
// head-scoping them is a timestamp comparison against the head commit's committedDate --
// see CollectHeadCommentAuthors. CreatedAt stays a raw ISO-8601 string: the GraphQL
// serializer options are deliberately NodaTime-free, same as GraphQlRateLimit.ResetAt.
public sealed record GraphQlIssueComment(GraphQlActor? Author, string? CreatedAt);
```

Change `GraphQlCommit` to add a trailing optional member:

```csharp
// CommittedDate is populated only on the head commit under commits(last: 1); the
// review/comment commit refs do not request it and leave it null.
public sealed record GraphQlCommit(string? Oid, GraphQlRollup? StatusCheckRollup, string? CommittedDate = null);
```

Change `GraphQlPageInfo`. Replace the existing record and its comment with:

```csharp
// hasNextPage for connections fetched with `first:`; hasPreviousPage for the comments
// connection, which is fetched with `last:` to get the most RECENT comments, so its
// overflow is at the opposite end. Reading the wrong flag fails silently -- it logs
// nothing and reports a clean pill. endCursor is not queried: there is no cursor
// pagination to consume it.
public sealed record GraphQlPageInfo(bool HasNextPage, bool HasPreviousPage = false);
```

Add a trailing member to `ReviewFactsPull`:

```csharp
public sealed record ReviewFactsPull(
    int Number,
    GraphQlActor? Author,
    NodeList<GraphQlLabel>? Labels,
    NodeList<GraphQlReview>? Reviews,
    NodeList<GraphQlThread>? ReviewThreads,
    NodeList<GraphQlCommitNode>? Commits,
    NodeList<GraphQlIssueComment>? Comments = null
);
```

Update the `NodeList<T>` doc comment, which currently claims the per-repo sweep never asks for `PageInfo` on nested connections — Task 4 makes that false:

```csharp
// PageInfo is optional and trailing. The per-repo sweep asks for it only on the comments
// connection, whose truncation would silently promote a pill to Clean; the exact-PR query
// asks for it everywhere, because once a query covers one pull request instead of
// twenty-five, a truncated thread list is affordable to detect and a silently wrong pill
// is not.
public sealed record NodeList<T>(IReadOnlyList<T>? Nodes, GraphQlPageInfo? PageInfo = null);
```

Add `HeadCommentAuthors` to `PrReviewFacts`, with its doc paragraph:

```csharp
/// <param name="HeadCommentAuthors">
/// Authors of ISSUE comments created strictly after the head commit. Separate from
/// <paramref name="HeadParticipatingAuthors"/> because it is weaker evidence: a comment
/// proves the reviewer spoke, not that it reviewed this diff. Only a reviewer explicitly
/// configured with CommentsCountAsParticipation may act on it.
/// </param>
public sealed record PrReviewFacts(
    int Number,
    string AuthorLogin,
    IReadOnlySet<string> Labels,
    IReadOnlyDictionary<string, int> UnresolvedThreadsByAuthor,
    IReadOnlySet<string> HeadParticipatingAuthors,
    IReadOnlySet<string> HeadCommentAuthors,
    IReadOnlySet<string> SuccessfulCheckAppSlugs
);
```

- [ ] **Step 4: Add the mapper collectors**

In `GitHubOrgClient.cs`, add the NodaTime using if absent:

```csharp
using NodaTime.Text;
```

Update `ToReviewFacts` to build and pass the new set:

```csharp
    public static PrReviewFacts ToReviewFacts(ReviewFactsPull pull)
    {
        var labels = CollectLabels(pull.Labels);
        var headOid = GetHeadOid(pull.Commits);
        var headParticipating = CollectReviewers(pull.Reviews, headOid);
        var unresolved = CollectThreadFacts(pull.ReviewThreads, headParticipating, headOid);
        var headComments = CollectHeadCommentAuthors(pull.Comments, pull.Commits);
        var checkApps = CollectSuccessfulCheckApps(pull.Commits);

        return new PrReviewFacts(
            pull.Number,
            pull.Author?.Login is { Length: > 0 } author ? author : "unknown",
            labels,
            unresolved,
            headParticipating,
            headComments,
            checkApps
        );
    }
```

Add these three helpers next to `GetHeadOid`:

```csharp
    // An issue comment carries no commit, so "did this land on the current head?" becomes
    // a timestamp comparison. Strict >: a comment stamped equal to the head commit is
    // ambiguous, and Pending is the safe direction. Anything unreadable -- no head date,
    // no timestamp, an unparseable one -- drops the author, never promotes them.
    private static HashSet<string> CollectHeadCommentAuthors(
        NodeList<GraphQlIssueComment>? comments,
        NodeList<GraphQlCommitNode>? commits
    )
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (GetHeadCommittedAt(commits) is not { } headAt)
        {
            return result;
        }
        foreach (var comment in comments?.Nodes ?? [])
        {
            if (comment.Author?.Login is { Length: > 0 } login && ParseInstant(comment.CreatedAt) is { } createdAt && createdAt > headAt)
            {
                _ = result.Add(login);
            }
        }
        return result;
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
```

- [ ] **Step 5: Fix every other PrReviewFacts construction site**

`PrReviewFacts` gained a positional parameter with no default, so all other construction sites now fail to compile. Find them:

```bash
grep -rn "new PrReviewFacts\|new(\s*181" --include=*.cs src tests
```

At minimum this covers the `Facts` helper in `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs` (Task 3 rewrites it) and any fixture in `ReviewSignalMergeTests.cs` / `ReviewSignalContractTests.cs`. For each, insert an empty `new HashSet<string>(StringComparer.OrdinalIgnoreCase)` in the new slot — between `headParticipating` and `checkApps` — unless the test is specifically about comment participation.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GitHubReviewFactsTests"`
Expected: PASS, including the seven new tests.

- [ ] **Step 7: Run the full suite to catch broken construction sites**

Run: `dotnet test`
Expected: PASS. A failure here means Step 5 missed a construction site.

- [ ] **Step 8: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubReviewFacts.cs src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs tests/FixPortal.Ci.Backend.Api.Tests
git commit -m "feat(review-signals): head-scope issue comments into review facts"
```

---

### Task 3: Factory reads comment participation

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Dashboard/Services/ReviewSignalFactory.cs` (`BuildReviewThreads`, around lines 81-104)
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs`

**Interfaces:**
- Consumes: `ReviewerOptions.CommentsCountAsParticipation` (Task 1), `PrReviewFacts.HeadCommentAuthors` (Task 2).
- Produces: the final observable behaviour. Nothing depends on this task.

- [ ] **Step 1: Write the failing tests**

In `ReviewSignalFactoryTests.cs`, extend the `Facts` helper for the new slot and add a reviewer fixture with the flag on:

```csharp
    private static readonly ReviewerOptions GitarWithComments = new()
    {
        Name = "Gitar",
        BotLogin = "gitar-app",
        CommentsCountAsParticipation = true,
    };

    private static PrReviewFacts Facts(
        IEnumerable<string>? labels = null,
        IDictionary<string, int>? unresolved = null,
        IEnumerable<string>? headParticipating = null,
        IEnumerable<string>? checkApps = null,
        IEnumerable<string>? headComments = null
    ) =>
        new(
            181,
            "chris",
            new HashSet<string>(labels ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(unresolved ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(headParticipating ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(headComments ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(checkApps ?? [], StringComparer.OrdinalIgnoreCase)
        );
```

Then add these tests:

```csharp
    [Fact]
    public void A_head_comment_is_ignored_unless_the_reviewer_opted_in()
    {
        // Gitar has the flag OFF. Comments must not silently become evidence for every
        // reviewer -- CodeRabbit posts chatty status comments it never intends as a pass.
        _ = Only(Gitar, Facts(headComments: ["gitar-app"])).State.Should().Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void Clean_when_an_opted_in_reviewer_left_a_head_comment_and_nothing_unresolved()
    {
        // The whole point: Gitar announces "no issues found" as an issue comment, submits
        // no review and opens no thread, and must still read Clean.
        var signal = Only(GitarWithComments, Facts(headComments: ["gitar-app"]));

        _ = signal.State.Should().Be(ReviewSignalState.Clean);
        _ = signal.Count.Should().BeNull();
        _ = signal.HtmlUrl.Should().BeNull();
    }

    [Fact]
    public void Unresolved_threads_still_outrank_an_opted_in_head_comment()
    {
        // Gitar opens threads for findings AND comments a summary. Outstanding must win,
        // or a finding would be masked by the very comment that reported it.
        var facts = Facts(
            unresolved: new Dictionary<string, int> { ["gitar-app"] = 2 },
            headComments: ["gitar-app"]
        );

        var signal = Only(GitarWithComments, facts);

        _ = signal.State.Should().Be(ReviewSignalState.Outstanding);
        _ = signal.Count.Should().Be(2);
    }

    [Fact]
    public void Another_bots_head_comment_does_not_make_this_reviewer_clean()
    {
        _ = Only(GitarWithComments, Facts(headComments: ["coderabbitai"]))
            .State.Should()
            .Be(ReviewSignalState.Pending);
    }

    [Fact]
    public void An_opted_in_reviewer_still_respects_the_required_label_gate()
    {
        var gated = new ReviewerOptions
        {
            Name = "Gitar",
            BotLogin = "gitar-app",
            RequiredLabel = "review-high",
            CommentsCountAsParticipation = true,
        };

        _ = Only(gated, Facts(headComments: ["gitar-app"])).State.Should().Be(ReviewSignalState.Disabled);
    }

    [Fact]
    public void Comment_participation_does_not_apply_to_a_code_scanning_reviewer()
    {
        // BuildCodeScanning is a separate path; the flag must not leak into it.
        var scanning = new ReviewerOptions
        {
            Name = "CodeQL",
            Source = ReviewerSource.CodeScanning,
            BotLogin = "github-code-scanning",
            CommentsCountAsParticipation = true,
        };

        _ = Only(scanning, Facts(headComments: ["github-code-scanning"]), 0)
            .State.Should()
            .Be(ReviewSignalState.Pending);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ReviewSignalFactoryTests"`
Expected: FAIL. `Clean_when_an_opted_in_reviewer_left_a_head_comment_and_nothing_unresolved` reports `Pending` where `Clean` was expected.

- [ ] **Step 3: Change the participation expression**

In `BuildReviewThreads`, replace:

```csharp
        var ran = facts.HeadParticipatingAuthors.Contains(login);
```

with:

```csharp
        // Two evidence channels, and the second is opt-in per reviewer. A reviewer that
        // reports findings as threads but announces a clean result as a plain comment
        // (Gitar, Code Quality) is otherwise pinned to Pending forever. This sits AFTER
        // the unresolved-thread return above, so a comment can only ever promote Pending
        // to Clean -- it can never mask an open finding.
        var ran =
            facts.HeadParticipatingAuthors.Contains(login)
            || (reviewer.CommentsCountAsParticipation && facts.HeadCommentAuthors.Contains(login));
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ReviewSignalFactoryTests"`
Expected: PASS, all tests including the six new ones.

- [ ] **Step 5: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Dashboard/Services/ReviewSignalFactory.cs tests/FixPortal.Ci.Backend.Api.Tests/Dashboard/ReviewSignalFactoryTests.cs
git commit -m "feat(review-signals): let an opted-in reviewer reach clean from a head comment"
```

---

### Task 4: Fetch comments in both GraphQL queries

**Files:**
- Modify: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs` (`ReviewFactsQuery` ~line 128, `ExactPrFragment` ~line 179, `WarnOnTruncatedConnections` ~line 403)
- Test: `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs`

**Interfaces:**
- Consumes: `GraphQlIssueComment`, `ReviewFactsPull.Comments`, `GraphQlPageInfo.HasPreviousPage` (Task 2).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

`WarnOnTruncatedConnections` is a private instance method, so pin the flag semantics directly rather than building logger-capture scaffolding for one assertion. Add:

```csharp
    [Fact]
    public void The_comments_connection_reports_truncation_on_has_previous_page()
    {
        // comments are fetched with `last:` to get the most RECENT ones, so overflow is at
        // the START of the connection. Asserting hasNextPage here would pass while the real
        // truncation went undetected -- silently, with a Clean pill on incomplete evidence.
        var truncated = new NodeList<GraphQlIssueComment>([], new GraphQlPageInfo(HasNextPage: false, HasPreviousPage: true));

        _ = truncated.PageInfo!.HasPreviousPage.Should().BeTrue();
        _ = truncated.PageInfo!.HasNextPage.Should().BeFalse();
    }
```

Then assert the queries actually request the fields, which is what this task delivers:

```csharp
    [Theory]
    [InlineData("comments(last: 20)")]
    [InlineData("createdAt")]
    [InlineData("committedDate")]
    [InlineData("hasPreviousPage")]
    public void Both_review_fact_queries_request_the_comment_fields(string fragment)
    {
        // The mapper cannot head-scope a comment it never received. Without this, Task 2's
        // collectors would sit correct and permanently starved of input.
        _ = GitHubOrgClient.ReviewFactsQueryText.Should().Contain(fragment);
        _ = GitHubOrgClient.ExactPrFragmentText.Should().Contain(fragment);
    }
```

This requires exposing the two query strings for test. In `GitHubOrgClient.cs`, add next to the existing private consts:

```csharp
    // Exposed for test: the queries are the contract between the mapper and GitHub, and a
    // field silently dropped from one of them starves a collector without failing anything.
    internal static string ReviewFactsQueryText => ReviewFactsQuery;

    internal static string ExactPrFragmentText => ExactPrFragment;
```

If the test project has no `InternalsVisibleTo` for the API assembly, make these `public` instead rather than adding assembly-level plumbing for two strings.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GitHubReviewFactsTests"`
Expected: FAIL — the queries contain none of the four fragments.

- [ ] **Step 3: Add the connection to the per-repo sweep query**

In `ReviewFactsQuery`, inside the `nodes {` block for a pull request, add after the `reviewThreads(...)` block:

```graphql
                comments(last: 20) {
                  # An issue comment is the only trace a reviewer leaves when it has
                  # nothing to report. `last:` because only the most recent matter, which
                  # is also why truncation shows up as hasPreviousPage, not hasNextPage.
                  nodes { author { login } createdAt }
                  pageInfo { hasPreviousPage }
                }
```

And add `committedDate` to the head commit selection in the same query:

```graphql
                commits(last: 1) {
                  nodes {
                    commit {
                      oid
                      committedDate
                      statusCheckRollup {
```

- [ ] **Step 4: Add the same to the exact-PR fragment**

In `ExactPrFragment`, add after the `reviewThreads(...)` block:

```graphql
          comments(last: 20) {
            nodes { author { login } createdAt }
            pageInfo { hasPreviousPage }
          }
```

And add `committedDate` to its head commit selection:

```graphql
          commits(last: 1) {
            nodes {
              commit {
                oid
                committedDate
                statusCheckRollup {
```

- [ ] **Step 5: Detect comment truncation**

In `WarnOnTruncatedConnections`, add after the `reviewThreads` check:

```csharp
        // hasPreviousPage, not hasNextPage: comments are fetched with `last:`.
        if (pull.Comments?.PageInfo?.HasPreviousPage == true)
        {
            truncated.Add("comments");
        }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GitHubReviewFactsTests"`
Expected: PASS.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubReviewFactsTests.cs
git commit -m "feat(review-signals): fetch pull request comments in both fact queries"
```

---

### Task 5: Turn the flag on for Gitar and Code Quality

**Files:**
- Modify: `deploy/bicep/main.bicep` (reviewer env vars, around lines 156-195)

**Interfaces:**
- Consumes: `ReviewerOptions.CommentsCountAsParticipation` (Task 1).
- Produces: nothing.

- [ ] **Step 1: Add the env var for Gitar**

After the `ReviewSignals__Reviewers__1__BotLogin` entry, add:

```bicep
            // Gitar reports findings as review threads but announces a clean result as a
            // plain issue comment, so without this it holds Pending on every PR it passes
            // -- on exactly the PRs that are ready to merge.
            {
              name: 'ReviewSignals__Reviewers__1__CommentsCountAsParticipation'
              value: 'true'
            }
```

- [ ] **Step 2: Add the env var for Code Quality**

After the `ReviewSignals__Reviewers__3__BotLogin` entry, add:

```bicep
            {
              name: 'ReviewSignals__Reviewers__3__CommentsCountAsParticipation'
              value: 'true'
            }
```

Leave reviewers `0` (CodeRabbit) and `2` (CodeQL) untouched. CodeRabbit submits genuine `APPROVED` reviews, so its participation already registers; CodeQL is a `CodeScanning` reviewer and never reaches this code path.

- [ ] **Step 3: Validate the template compiles**

Run: `az bicep build --file deploy/bicep/main.bicep --stdout > $null`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add deploy/bicep/main.bicep
git commit -m "feat(deploy): count comments as participation for Gitar and Code Quality"
```

---

## Verification before pushing

The house gate for this repo, run from the repo root. All must pass before the single push:

- [ ] `dotnet build` — 0 warnings, 0 errors
- [ ] `dotnet test` — full suite green
- [ ] `az bicep build --file deploy/bicep/main.bicep --stdout > $null` — template valid

## Post-merge verification

Not automatable from this session — it needs the deployed Container App. After the deploy lands, confirm on a real pull request that Gitar has approved by comment:

```powershell
az containerapp show -n fixportal-ci-backend -g <resource-group> --query "properties.template.containers[0].env"
```

Then reload the board and confirm the Gitar pill reads clean (filled round dot, green tint) rather than pending (hollow dot, dashed) on a PR where `gh pr view <N> --json reviews` returns an empty array but `gitar-bot` has commented since the last push.

## Known gap, deliberately out of scope

CodeQL reads `Pending` on `fixportal-engine#219` despite a green scan. That is the `BuildCodeScanning` path, not this one — either `openAlerts` came back null or the head commit's successful checks lack the `github-code-scanning` app slug. It needs its own investigation and is not addressed here.
