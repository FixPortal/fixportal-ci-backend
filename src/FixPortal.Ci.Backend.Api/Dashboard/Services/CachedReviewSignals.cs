using FixPortal.Ci.Backend.Api.Dashboard.Model;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// One pull request's cached review signals together with the head commit they were
/// computed against. Review state is only meaningful relative to a head, so the cache
/// entry must carry it: <c>DashboardRefreshService.ApplyReviewSignals</c> attaches the
/// signals only when <see cref="HeadSha"/> matches the head the fresh open-PR listing
/// reports. A push between sweeps then degrades the pull request to "unknown" instead
/// of republishing a Clean verdict a reviewer never earned against the new head.
/// </summary>
/// <param name="HeadSha">
/// The head the signals were computed against — taken from the GraphQL facts' own
/// <c>commits(last: 1)</c> oid when the pull request was refetched, carried over
/// unchanged when it was not. Null means the facts did not identify a head; such an
/// entry never attaches.
/// </param>
public sealed record CachedReviewSignals(string? HeadSha, IReadOnlyList<ReviewSignal> Signals);
