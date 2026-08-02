using FixPortal.Ci.Backend.Api.Dashboard.Model;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Covers the transitions the REST watermark cannot see.
///
/// <para>
/// The watermark diff is free and catches almost everything, because pushes, reviews,
/// review comments and label changes all bump a pull request's <c>updated_at</c>. Two
/// pill transitions are not reliably visible to it:
/// </para>
/// <list type="bullet">
/// <item><b>Outstanding -&gt; Clean.</b> Resolving a review thread does not clearly bump
/// <c>updated_at</c>. Treated as invisible until measured — see the note on
/// <see cref="RefreshInterval"/>.</item>
/// <item><b>Pending -&gt; Clean.</b> A check suite completing against an unchanged head
/// bumps nothing, and a clean code-scanning result leaves the alerts payload identical
/// (still zero), so that endpoint's ETag does not fire either.</item>
/// </list>
///
/// <para>
/// Both are exactly the transitions someone is sitting and waiting for, so leaving them
/// to the slow reconciliation pass would strand the pill in the state that matters most.
/// Instead, any pull request whose signals are non-terminal is refetched on a timer
/// regardless of its watermark. Only a handful of pull requests are ever in those states,
/// so this spends a few points an hour, precisely where a human is watching.
/// </para>
/// </summary>
public static class ReviewSignalPriority
{
    /// <summary>
    /// A signal in one of these states can still change without the watermark moving.
    /// Clean and Disabled are terminal for a given head: Clean can only be undone by a
    /// push, and a push moves the head, which the watermark does see.
    /// </summary>
    public static bool IsNonTerminal(ReviewSignal signal) =>
        signal.State is ReviewSignalState.Outstanding or ReviewSignalState.Pending;

    /// <summary>
    /// Which of the currently-cached pull requests are due a watermark-independent
    /// refetch. Pure over (signals, last observation, interval, now) so the policy is
    /// testable without a worker or a live clock.
    /// </summary>
    /// <param name="signalsByPr">Current cached signals, keyed by pull request number.</param>
    /// <param name="observedAt">When each pull request was last fetched.</param>
    /// <param name="refreshInterval">How stale a non-terminal pill may get.</param>
    /// <param name="now">Injected clock reading.</param>
    public static IReadOnlyList<int> SelectDue(
        IReadOnlyDictionary<int, IReadOnlyList<ReviewSignal>> signalsByPr,
        IReadOnlyDictionary<int, Instant> observedAt,
        Duration refreshInterval,
        Instant now,
        Duration? reconcileInterval = null
    )
    {
        var reconcile = reconcileInterval ?? ReconcileInterval;
        var due = new List<int>();
        foreach (var (number, signals) in signalsByPr)
        {
            // Terminal pills are not immune to going stale, they just go stale slowly:
            // a check can re-run and fail against an UNCHANGED head, turning a Clean pill
            // wrong without moving updated_at or the head SHA. Nothing free can see that,
            // so everything is reconciled eventually — non-terminal pills just far sooner.
            var interval = signals.Any(IsNonTerminal) ? refreshInterval : reconcile;

            // Never observed but cached is a contradiction; refetch rather than assume.
            if (!observedAt.TryGetValue(number, out var seen) || now - seen >= interval)
            {
                due.Add(number);
            }
        }

        due.Sort();
        return due;
    }

    /// <summary>
    /// Default staleness ceiling for a non-terminal pill. Deliberately coarse: this is a
    /// fallback for transitions the watermark misses, not the primary refresh path.
    /// </summary>
    /// <remarks>
    /// Tune this once it is known whether resolving a review thread bumps a pull
    /// request's <c>updated_at</c>. If it does, the Outstanding -&gt; Clean case is
    /// watermark-visible after all and this interval can lengthen considerably; the
    /// Pending -&gt; Clean case still needs it. Measuring that is one API call against a
    /// pull request whose thread was just resolved — it has not been done, so the
    /// conservative interval stands.
    /// </remarks>
    public static readonly Duration RefreshInterval = Duration.FromMinutes(10);

    /// <summary>
    /// Backstop for pull requests whose pills are all terminal. This is the whole
    /// reconciliation story: rather than a synchronised full sweep, every open pull
    /// request is simply re-observed once an hour, spread across sweeps by whenever each
    /// was last seen. With a handful of open pull requests that is a few points an hour,
    /// and it degrades gracefully — the reserve floor caps it like any other spend.
    /// </summary>
    public static readonly Duration ReconcileInterval = Duration.FromHours(1);
}
