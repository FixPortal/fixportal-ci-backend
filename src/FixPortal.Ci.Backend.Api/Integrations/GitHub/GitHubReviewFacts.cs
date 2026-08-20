namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

// GraphQL wire shapes. Deserialized with a CAMEL-CASE serializer, unlike every REST
// DTO in this project (which uses SnakeCaseLower) — GitHub's GraphQL API returns
// camelCase field names. Using the REST options object here yields all-null
// properties and no error, so the two must not be mixed.
public sealed record GraphQlEnvelope<T>(T? Data, IReadOnlyList<GraphQlError>? Errors);

public sealed record GraphQlError(string? Message);

// PageInfo is optional and trailing. The per-repo sweep asks for it only on the comments
// connection, and only as a diagnostic, not a safety mechanism: `last: 20` drops the
// OLDEST comments, and HeadCommentAuthors is built solely from comments that were
// actually returned, so truncation can only shrink that set -- it produces Pending, never
// a false Clean. hasPreviousPage exists so a chatty pull request is observable rather
// than mysteriously stuck. The exact-PR query asks for PageInfo everywhere, because once
// a query covers one pull request instead of twenty-five, a truncated thread list is
// affordable to detect -- and there, unlike comments, truncation IS unsafe: a missing
// unresolved thread reads as a confident Clean.
public sealed record NodeList<T>(IReadOnlyList<T>? Nodes, GraphQlPageInfo? PageInfo = null);

public sealed record GraphQlActor(string? Login);

public sealed record GraphQlLabel(string? Name);

public sealed record GraphQlReview(GraphQlActor? Author, GraphQlCommit? Commit);

// OriginalCommit (not Commit): GitHub's `commit` field on a review comment is the
// commit the comment CURRENTLY applies to and can advance as the PR is pushed to;
// `originalCommit` is the commit it was authored against. Head-scoping needs the
// latter, or a thread opened on commit 1 would report the head oid and wrongly
// count as head participation.
public sealed record GraphQlComment(GraphQlActor? Author, GraphQlCommit? OriginalCommit);

// An ISSUE comment on the pull request, distinct from GraphQlComment (a REVIEW comment,
// which anchors to a commit). Issue comments carry no commit reference at all, so
// head-scoping them compares CreatedAt against when the head CHANGED (tracked by the
// enrichment worker from the watermark's head-SHA transitions), not against the head
// commit's committedDate -- a commit is authored before it is pushed, so committedDate
// would certify comments written before the head they claim to cover existed on the PR.
// See CollectHeadCommentAuthors. CreatedAt stays a raw ISO-8601 string: the GraphQL
// serializer options are deliberately NodaTime-free, same as GraphQlRateLimit.ResetAt.
//
// LastEditedAt matters because a reviewer's verdict does not have to arrive in a NEW
// comment. Gitar posts one dashboard comment within seconds of the pull request opening
// -- at that point it says only that automatic review is paused -- and then EDITS that
// same comment in place with the verdict a minute or two later, and again on every
// re-review. Reading CreatedAt alone dates the placeholder, never the review, so the
// comment channel could not certify a reviewer that never posts twice.
public sealed record GraphQlIssueComment(GraphQlActor? Author, string? CreatedAt, string? LastEditedAt = null);

public sealed record GraphQlThread(bool IsResolved, NodeList<GraphQlComment>? Comments);

public sealed record GraphQlApp(string? Slug);

public sealed record GraphQlCheckSuite(GraphQlApp? App);

// Only the conclusion and the publishing app matter: the check-run NAME is never read
// (a reviewer is matched by app slug, not by check title), so it is neither queried
// nor carried here.
public sealed record GraphQlContext(string? Conclusion, GraphQlCheckSuite? CheckSuite);

public sealed record GraphQlRollup(NodeList<GraphQlContext>? Contexts);

// Oid is populated at every usage site (the review/comment commit ref and the head
// commit under commits(last: 1)); StatusCheckRollup is only populated on the latter.
// CommittedDate is populated only on the head commit under commits(last: 1); the
// review/comment commit refs do not request it and leave it null.
public sealed record GraphQlCommit(string? Oid, GraphQlRollup? StatusCheckRollup, string? CommittedDate = null);

public sealed record GraphQlCommitNode(GraphQlCommit? Commit);

public sealed record ReviewFactsPull(
    int Number,
    GraphQlActor? Author,
    NodeList<GraphQlLabel>? Labels,
    NodeList<GraphQlReview>? Reviews,
    NodeList<GraphQlThread>? ReviewThreads,
    NodeList<GraphQlCommitNode>? Commits,
    NodeList<GraphQlIssueComment>? Comments = null
);

// hasNextPage for connections fetched with `first:`; hasPreviousPage for the comments
// connection, which is fetched with `last:` to get the most RECENT comments, so its
// overflow is at the opposite end -- under the Relay spec, hasNextPage is only
// guaranteed accurate when paginating with `first:`. Reading the wrong flag here fails
// silently: it logs nothing. But because `last:` truncation drops the OLDEST comments,
// the cost of missing it is a missed author and a pill stuck on Pending, never a false
// Clean. endCursor is not queried: there is no cursor pagination to consume it.
public sealed record GraphQlPageInfo(bool HasNextPage, bool HasPreviousPage = false);

public sealed record ReviewFactsPullConnection(IReadOnlyList<ReviewFactsPull>? Nodes, GraphQlPageInfo? PageInfo);

public sealed record ReviewFactsRepository(ReviewFactsPullConnection? PullRequests);

/// <summary>
/// GraphQL's own rate-limit accounting for the query that returned it. GraphQL is
/// metered on a separate 5,000-points/hour budget from REST, priced by connection
/// fan-out rather than by request, so <paramref name="Cost"/> is the only honest
/// measure of what one sweep of this feature spends. ResetAt is left as the raw
/// ISO-8601 string: the GraphQL serializer options are deliberately NodaTime-free.
/// </summary>
public sealed record GraphQlRateLimit(int Cost, int Remaining, string? ResetAt);

public sealed record ReviewFactsData(ReviewFactsRepository? Repository, GraphQlRateLimit? RateLimit);

/// <summary>
/// Response shape for the exact-PR query, whose <c>repository</c> object holds one
/// aliased <c>pullRequest(number:)</c> field per requested pull request rather than a
/// fixed set of properties — hence a dictionary keyed by alias. A null value means
/// GitHub returned no such pull request (closed and re-listed, or a race with the REST
/// listing), which is not an error.
/// </summary>
public sealed record ExactReviewFactsData(
    IReadOnlyDictionary<string, ReviewFactsPull?>? Repository,
    GraphQlRateLimit? RateLimit
);

/// <summary>
/// Outcome of an exact-PR fetch: what was read, what could not be, and how many queries
/// it took.
/// </summary>
/// <param name="Failed">
/// Pull requests GitHub refused. Their watermark MUST NOT be advanced by the caller, or
/// the next diff comes back clean and the failure is silently certified as current — the
/// same trap as committing a watermark before a call that can still throw.
/// </param>
/// <param name="QueriesIssued">
/// Counts ATTEMPTS, including rejected ones. Counting only successes made a sweep that
/// was being refused on every single query report "0 GraphQL queries", indistinguishable
/// from a quiet sweep that legitimately spent nothing. That reading cost real debugging
/// time on the day it shipped.
/// </param>
/// <param name="PointsSpent">
/// Summed across every query in the batch. Reading the cost off the LAST rate-limit
/// observation instead loses everything the retry path spent: a refused batch that
/// re-queries nineteen pull requests individually would report only the final one's cost,
/// understating spend in exactly the scenario the retry exists for.
/// </param>
public sealed record ReviewFactsBatch(
    IReadOnlyDictionary<int, PrReviewFacts> Facts,
    IReadOnlyList<int> Failed,
    int QueriesIssued,
    int PointsSpent
)
{
    public static readonly ReviewFactsBatch Empty = new(new Dictionary<int, PrReviewFacts>(), [], 0, 0);
}

/// <summary>
/// Everything needed to decide one pull request's reviewer states, flattened out of
/// the GraphQL payload. Deliberately reviewer-agnostic: it records who did what, and
/// the factory decides what that means for a configured reviewer.
/// </summary>
/// <param name="HeadParticipatingAuthors">
/// Authors that reviewed or opened a thread ON THE CURRENT HEAD COMMIT. Participation
/// on an older commit does not count: a rate-limited or auto-paused bot can still hold
/// a passing check or a stale review from three pushes ago, and neither is evidence it
/// ran against what is actually on the PR now.
/// </param>
/// <param name="HeadCommentAuthors">
/// Authors of ISSUE comments last written -- created, or EDITED -- after the head commit
/// became the head. Separate from
/// <paramref name="HeadParticipatingAuthors"/> because it is weaker evidence: a comment
/// proves the reviewer spoke, not that it reviewed this diff. Only a reviewer explicitly
/// configured with CommentsCountAsParticipation may act on it.
/// </param>
/// <param name="HeadSha">
/// The head commit oid these facts were computed against, from the query's own
/// <c>commits(last: 1)</c> node. Consumers attach cached signals only when this matches
/// the head the open-PR listing currently reports.
/// </param>
/// <param name="TruncatedConnections">
/// Names of connections that hit their page cap while these facts were collected
/// ("reviewThreads", "checkContexts", ...). A truncated connection means the flattened
/// sets below may be incomplete; consumers must not let affected signals read Clean.
/// Null or empty means every connection answered in full.
/// </param>
public sealed record PrReviewFacts(
    int Number,
    string AuthorLogin,
    IReadOnlySet<string> Labels,
    IReadOnlyDictionary<string, int> UnresolvedThreadsByAuthor,
    IReadOnlySet<string> HeadParticipatingAuthors,
    IReadOnlySet<string> HeadCommentAuthors,
    IReadOnlySet<string> SuccessfulCheckAppSlugs,
    string? HeadSha = null,
    IReadOnlySet<string>? TruncatedConnections = null
);
