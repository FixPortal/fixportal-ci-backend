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
recent, so overflow is at the opposite end, and under the Relay spec `hasNextPage` is
only guaranteed accurate when paginating with `first:`. `GraphQlPageInfo` gains
`HasPreviousPage`, and `WarnOnTruncatedConnections` reads the correct flag for this
connection. This check is a diagnostic, not a safety mechanism: `last: 20` drops the
OLDEST comments, and `HeadCommentAuthors` is built only from comments GitHub actually
returned, so truncation can only shrink that set, and can only push a pill toward
`Pending` — never toward a false `Clean`. Reading the wrong flag would fail silently, in
that it would log nothing, but the failure mode is a missed author, not a certified
pass; `hasPreviousPage` is kept because a chatty pull request deserves to be observable
rather than mysteriously stuck, not because getting it wrong would be unsafe.

### Error handling

Every path where the input needed to evaluate participation cannot be read resolves to
`Pending`, never `Clean`:

- comment connection truncated
- comment connection absent or null
- `createdAt` or `committedDate` missing or unparseable
- flag set with no `BotLogin` (also rejected at startup)

This matches the existing treatment of unreadable code-scanning alerts at
`ReviewSignalFactory.cs:67-70`, where "unreadable" is `Pending` rather than `Clean`.

That is the full set of paths where the input is unreadable. It is not the full set of
paths where the resulting signal can be wrong. Two limitations are accepted rather than
mitigated, because closing either would mean the comment-body parsing the Approach
section above already rejected.

The first is that any comment counts, not just a verdict comment. The same objection
that ruled out trusting a successful check — "a 'review rate limited' check passes by
design, so this would certify a pass nobody performed" — applies just as much to the
comment channel: a Gitar comment reading "review paused, resume next week", "rate
limited", or "reviewing this PR..." is indistinguishable, on presence alone, from an
approval, and promotes the pill to `Clean`. Telling a status comment from a verdict
would need comment-body parsing, which "No comment-body parsing" in Out of scope
declines to do, for the same reason it was rejected as an alternative above: it
couples the backend to each vendor's comment formatting, and a restyle would silently
flip pills with nothing to detect it.

The second is that `committedDate` is commit-creation time, not push time. A
force-push that resets the branch to an earlier commit — backing out a bad push —
leaves the head commit's date behind comments that already exist, so those stale
comments satisfy the strict `>` and read as head-scoped. `git rebase
--committer-date-is-author-date` has the same shape, because it deliberately keeps
the original commit time instead of stamping a fresh one. This is distinct from an
ordinary rebase or `--amend`, a merge of main into the branch, an edited comment
(which keeps its original `createdAt`, so editing only makes it staler), clock skew
(both timestamps are GitHub-server-issued), or a bot commenting before a fixup push —
all of those advance the head commit's date past the existing comments and fail safe
to `Pending`, so they are not listed as hazards here.

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
