namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

// GraphQL wire shapes. Deserialized with a CAMEL-CASE serializer, unlike every REST
// DTO in this project (which uses SnakeCaseLower) — GitHub's GraphQL API returns
// camelCase field names. Using the REST options object here yields all-null
// properties and no error, so the two must not be mixed.
public sealed record GraphQlEnvelope<T>(T? Data, IReadOnlyList<GraphQlError>? Errors);

public sealed record GraphQlError(string? Message);

// PageInfo is optional and trailing: the per-repo sweep never asks for it on nested
// connections, so it stays null there. The exact-PR query does ask, because once a query
// covers one pull request instead of twenty-five, a truncated thread list is affordable
// to detect and a silently wrong pill is not.
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
public sealed record GraphQlCommit(string? Oid, GraphQlRollup? StatusCheckRollup);

public sealed record GraphQlCommitNode(GraphQlCommit? Commit);

public sealed record ReviewFactsPull(
    int Number,
    GraphQlActor? Author,
    NodeList<GraphQlLabel>? Labels,
    NodeList<GraphQlReview>? Reviews,
    NodeList<GraphQlThread>? ReviewThreads,
    NodeList<GraphQlCommitNode>? Commits
);

// hasNextPage only — the open-PR connection is deliberately capped at the 50 most
// recently updated (see ReviewFactsQuery in GitHubOrgClient), and the flag exists so
// an overflowing repo is observable rather than silently truncated. endCursor is not
// queried: there is no cursor pagination to consume it.
public sealed record GraphQlPageInfo(bool HasNextPage);

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
public sealed record PrReviewFacts(
    int Number,
    string AuthorLogin,
    IReadOnlySet<string> Labels,
    IReadOnlyDictionary<string, int> UnresolvedThreadsByAuthor,
    IReadOnlySet<string> HeadParticipatingAuthors,
    IReadOnlySet<string> SuccessfulCheckAppSlugs
);
