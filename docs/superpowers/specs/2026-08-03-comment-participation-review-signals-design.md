# Comment participation for review signals

**Date:** 2026-08-03
**Status:** Approved

## Problem

A review pill can never reach `Clean` for a reviewer whose clean result is announced
as a plain issue comment rather than a submitted review.

`ReviewSignalFactory.BuildReviewThreads` requires positive evidence that a reviewer
ran before reporting `Clean`, and defines that evidence as
`facts.HeadParticipatingAuthors.Contains(login)` — authors that reviewed or opened a
review thread on the current head commit. The rule is correct and deliberate: a
passing check is not evidence of a review, because CodeRabbit's "rate limited" and
"review skipped" checks pass by design so they never block a protected-branch merge.

The gap is in what counts as participation, not in the rule. Observed behaviour across
the estate:

| Reviewer outcome | What GitHub records | Current pill |
|---|---|---|
| Has findings | A review (`COMMENTED`) plus review threads | `Outstanding` — correct |
| Findings all resolved | Threads resolved, review still on record | `Clean` — correct |
| Nothing to report | An issue comment only; no review, no thread | `Pending` — **wrong** |

Confirmed on `fixportal-ci-backend#72`, which carries `gitar-bot` and
`github-code-quality` reviews with matching review threads, and on
`fixportal-engine#219`, where Gitar posted an approving issue comment, submitted no
review, opened no thread, and rendered `Pending`.

The consequence is that Gitar — the routine reviewer, required at both HIGH and NORMAL
tier — reads `Pending` on every pull request it passes. The board reports "not yet
reviewed" on precisely the pull requests that are ready to merge, which is worse than
reporting nothing: it trains the reader to ignore the pill.

CodeRabbit is unaffected. It submits genuine `APPROVED` reviews
(`fixportal-ci-backend#74`, `#72`, `#70`, `#68`), so its participation already
registers.

## Approach

Treat a head-scoped issue comment as an additional, opt-in channel of participation
evidence, layered onto the existing review-thread logic.

Three alternatives were considered and rejected:

- **Trusting a successful check from the reviewer's own app.** Free — the app slugs
  are already fetched — but it reverses the explicit decision recorded at
  `ReviewSignalFactory.cs:97-99`. A "review rate limited" check passes by design, so
  this would certify a pass nobody performed.
- **Parsing the comment body for a verdict marker.** Most precise, and could
  distinguish clean from outstanding, but it couples the backend to each vendor's
  comment formatting. A restyle would silently flip pills with nothing to detect it.
- **A third `ReviewerSource` value.** `Source` is exclusive — `ReviewThreads` XOR
  `CodeScanning`, dispatched at `ReviewSignalFactory.cs:54-56`. A reviewer moved to an
  `IssueComments` source would lose `Outstanding`, because that state is derived from
  unresolved threads. Gitar needs both channels, so the new evidence must be additive.

### Why layering is safe

`BuildReviewThreads` returns `Outstanding` on `unresolved > 0` before it evaluates
participation. A comment-derived signal can therefore only ever promote `Pending` to
`Clean`, and only when there are no unresolved threads. It cannot mask a finding.

## Design

### Configuration

`ReviewerOptions` gains one optional flag, defaulting to `false`:

```csharp
/// <summary>
/// When set, an issue comment from BotLogin dated after the head commit also counts
/// as participation. For reviewers that report findings as review threads but
/// announce a clean result as a plain comment, which otherwise pins them to Pending
/// forever.
/// </summary>
public bool CommentsCountAsParticipation { get; init; }
```

Startup validation mirrors the existing `BotLogin` rule: the flag requires a non-blank
`BotLogin`, or it can never match and would report `Pending` forever — the same silent
misconfiguration the existing validator exists to prevent.

Deployment sets two env vars in `fixportal-ci-backend/deploy/bicep/main.bicep`. No
existing reviewer entry changes:

```
ReviewSignals__Reviewers__1__CommentsCountAsParticipation = true   # Gitar
ReviewSignals__Reviewers__3__CommentsCountAsParticipation = true   # Code Quality
```

CodeQL (`Source=CodeScanning`) and CodeRabbit are left alone.

### Facts

`PrReviewFacts` gains one member alongside `HeadParticipatingAuthors`:

```csharp
IReadOnlySet<string> HeadCommentAuthors
```

Authors of issue comments created strictly after the head commit's `committedDate`.

All timestamp handling stays in the mapper; the factory never sees a date. This keeps
`PrReviewFacts` reviewer-agnostic — it records who did what, and the factory decides
what that means — and confines time parsing to one place.

Wire records stay NodaTime-free, following the `GraphQlRateLimit.ResetAt` precedent:
`createdAt` and `committedDate` are carried as raw ISO-8601 strings and parsed to a
NodaTime `Instant` at the mapping boundary.

Comparison is strict `>`. A comment timestamped equal to the head commit is excluded,
because the safe direction is `Pending`.

### Factory

The entire behaviour change, in `BuildReviewThreads`, after the `unresolved > 0` early
return:

```csharp
var ran = facts.HeadParticipatingAuthors.Contains(login)
    || (reviewer.CommentsCountAsParticipation && facts.HeadCommentAuthors.Contains(login));
```

`BuildCodeScanning` and the `RequiredLabel` / `Disabled` gate are untouched.

### GraphQL

Both `ReviewFactsQuery` and `ExactPrFragment` gain the comment connection, and the head
commit gains its date:

```graphql
comments(last: 20) { nodes { author { login } createdAt } pageInfo { hasPreviousPage } }
commits(last: 1) { nodes { commit { oid committedDate ... } } }
```

Fetching in both queries — rather than only the exact-PR query — keeps the facts
identical regardless of which path populated them. Splitting them would let the same
pull request read `Pending` from the sweep and `Clean` from the exact-PR fetch, a pill
that flickers with fetch route. Comments are a flat connection with no nested fan-out,
so the marginal cost on the 25-PR sweep is small.

This makes the per-repo sweep the first place that requests `pageInfo` on a nested
connection, which the `NodeList<T>` doc comment currently states it never does. That
comment must be updated as part of the change rather than left contradicting the query.

**Truncation is detected with `hasPreviousPage`, not `hasNextPage`.** This is the one
place the existing convention inverts. Every other connection in these queries uses
`first:`, where overflow appears as `hasNextPage`; comments use `last:` to get the most
recent, so overflow is at the opposite end. `GraphQlPageInfo` gains `HasPreviousPage`,
and `WarnOnTruncatedConnections` reads the correct flag for this connection. Reading
the wrong one fails silently — it logs nothing and reports a clean pill.

### Error handling

Every failure path resolves to `Pending`, never `Clean`:

- comment connection truncated
- comment connection absent or null
- `createdAt` or `committedDate` missing or unparseable
- flag set with no `BotLogin` (also rejected at startup)

This matches the existing treatment of unreadable code-scanning alerts at
`ReviewSignalFactory.cs:67-70`, where "unreadable" is `Pending` rather than `Clean`.

## Testing

`ReviewSignalFactoryTests`

- flag off, head comment present, no review or thread — stays `Pending`
- flag on, head comment present, no unresolved threads — `Clean`
- flag on, comment older than the head commit — stays `Pending`
- flag on, head comment present, unresolved threads present — `Outstanding` still wins
- flag on with no `BotLogin` — stays `Pending`
- flag on, `RequiredLabel` absent from the PR — still `Disabled`

`ReviewSignalsConfigBindingTests`

- flag binds from the `ReviewSignals__Reviewers__n__CommentsCountAsParticipation` shape
- validation rejects the flag without a `BotLogin`

`ReviewSignalMergeTests` / `ReviewSignalContractTests`

- `HeadCommentAuthors` survives the merge path
- the snapshot contract the frontend consumes is unchanged

Mapper tests

- `hasPreviousPage` on the comment connection produces a truncation warning
- an unparseable timestamp excludes the author rather than throwing
- a comment exactly equal to `committedDate` is excluded

## Out of scope

- **No frontend change.** `fixportal-ci-frontend` already renders `clean`; the pill
  states and CSS exist and are unchanged.
- **No comment-body parsing.** Presence of a head-scoped comment is the whole signal.
- **No change to CodeQL's `Pending` state on `fixportal-engine#219`.** That has a
  separate cause — either unreadable alerts or a missing `github-code-scanning` app
  slug in the head commit's successful checks — and is tracked separately.
- **No change to the `Source` enum**, `BuildCodeScanning`, or the `Disabled` gate.
