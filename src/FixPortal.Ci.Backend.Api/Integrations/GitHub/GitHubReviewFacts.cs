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
