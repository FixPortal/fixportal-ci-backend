using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// What the cheap REST open-PR listing tells us about a pull request, reduced to the
/// two fields that decide whether its review signal can still be trusted.
/// </summary>
/// <param name="UpdatedAt">
/// Bumped by pushes, reviews, review comments, and label changes — all of the
/// transitions a pill renders. Not bumped by everything, which is why
/// <see cref="ReviewSignalPriority"/> exists.
/// </param>
/// <param name="HeadSha">
/// The head the signal was observed against. Review state is only meaningful relative
/// to a head, so a moved head invalidates a signal even if nothing else changed.
/// </param>
public readonly record struct PrWatermark(Instant? UpdatedAt, string? HeadSha)
{
    /// <summary>
    /// True when the listing gave us neither field, so this watermark can prove nothing.
    /// Treated as dirty rather than clean: an unknown watermark must cost a query, never
    /// silently certify a stale pill.
    /// </summary>
    public bool IsUnknown => UpdatedAt is null && string.IsNullOrEmpty(HeadSha);
}

/// <summary>Pull requests needing a refetch, and those that have gone away.</summary>
public sealed record WatermarkDiff(IReadOnlyList<int> Dirty, IReadOnlyList<int> Evicted)
{
    public static readonly WatermarkDiff Empty = new([], []);

    public bool IsEmpty => Dirty.Count == 0 && Evicted.Count == 0;
}

/// <summary>
/// Decides which pull requests changed since the last observation. This is the whole
/// point of the incremental design: the previous implementation swept every repository
/// on a timer and paid full GraphQL fan-out to re-derive a diff the REST listing already
/// shows for free (GitHub does not charge a conditional 304 against the REST budget).
/// Pure and static so the decision is testable without a worker, a clock, or HTTP.
/// </summary>
public static class PrWatermarkDiff
{
    public static WatermarkDiff Compute(
        IReadOnlyDictionary<int, PrWatermark> previous,
        IReadOnlyDictionary<int, PrWatermark> current
    )
    {
        var dirty = new List<int>();
        foreach (var (number, now) in current)
        {
            // Unknown on either side means we cannot prove the signal still holds, so we
            // pay for a query. Erring towards spending is safe here — the reserve floor
            // bounds the damage, whereas erring towards trusting renders a wrong pill.
            if (!previous.TryGetValue(number, out var before) || before.IsUnknown || now.IsUnknown || before != now)
            {
                dirty.Add(number);
            }
        }

        var evicted = previous.Keys.Where(number => !current.ContainsKey(number)).ToList();

        // Deterministic order so a sweep's spend is reproducible and its logs diffable.
        dirty.Sort();
        evicted.Sort();
        return new WatermarkDiff(dirty, evicted);
    }
}
