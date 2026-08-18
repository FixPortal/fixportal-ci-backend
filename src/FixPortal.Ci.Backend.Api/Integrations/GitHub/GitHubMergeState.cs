namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

/// <summary>
/// GitHub's own verdict on whether a pull request can be merged, as raw strings rather
/// than enums. The snapshot boundary does no runtime validation and GitHub adds
/// enum members over time, so an unrecognised value must fall through to "not ready"
/// rather than fail to deserialize or, worse, land on whichever enum member happens to
/// be zero.
/// </summary>
/// <param name="Mergeable">MERGEABLE, CONFLICTING or UNKNOWN.</param>
/// <param name="MergeStateStatus">
/// CLEAN, BLOCKED, BEHIND, DIRTY, DRAFT, HAS_HOOKS, UNSTABLE or UNKNOWN. Only meaningful
/// once the repository declares required status checks: without them GitHub has no
/// opinion about CI, and every conflict-free non-draft pull request reports CLEAN.
/// </param>
/// <param name="HeadSha">
/// The head commit (<c>headRefOid</c>) this verdict was read against. A verdict earned
/// against a different head says nothing about the current one; the ready-to-merge
/// calculator treats a mismatch — or an absent value — as unknown, never as ready.
/// </param>
public sealed record PrMergeState(
    int Number,
    bool IsDraft,
    string? Mergeable,
    string? MergeStateStatus,
    string? HeadSha = null
)
{
    /// <summary>
    /// True only when GitHub says the pull request is mergeable with nothing outstanding.
    ///
    /// <para>
    /// CLEAN alone, deliberately. HAS_HOOKS and UNSTABLE are both "mergeable, but
    /// something is off" — UNSTABLE in particular means a non-required check is failing,
    /// and a filter whose whole purpose is "safe to merge" should not surface those.
    /// UNKNOWN is not a synonym for false: GitHub computes the merge state lazily and
    /// answers UNKNOWN on a cold ask, which is why the caller distinguishes
    /// not-yet-known from known-not-ready.
    /// </para>
    /// </summary>
    public bool IsMergeClean =>
        !IsDraft
        && string.Equals(Mergeable, "MERGEABLE", StringComparison.Ordinal)
        && string.Equals(MergeStateStatus, "CLEAN", StringComparison.Ordinal);

    /// <summary>
    /// True when GitHub has not computed the merge state yet. Such a pull request must
    /// report "unknown" rather than "not ready", or a freshly-pushed branch would flicker
    /// out of the board's ready filter for no reason the user can see.
    /// </summary>
    public bool IsUndetermined =>
        string.IsNullOrEmpty(MergeStateStatus)
        || string.Equals(MergeStateStatus, "UNKNOWN", StringComparison.Ordinal)
        || string.Equals(Mergeable, "UNKNOWN", StringComparison.Ordinal);
}

/// <summary>GraphQL wire shape for one aliased pull request in the merge-state query.</summary>
public sealed record MergeStatePull(
    int Number,
    bool IsDraft,
    string? Mergeable,
    string? MergeStateStatus,
    string? HeadRefOid
);

/// <summary>
/// Response shape for the merge-state query. Like the review-facts equivalent, the
/// <c>repository</c> object holds one aliased <c>pullRequest(number:)</c> field per
/// requested pull request, so it is a dictionary keyed by alias. A null value means
/// GitHub returned no such pull request — closed since the listing, not an error.
/// </summary>
public sealed record MergeStateData(
    IReadOnlyDictionary<string, MergeStatePull?>? Repository,
    GraphQlRateLimit? RateLimit
);
