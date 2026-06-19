using System.Collections.Concurrent;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

/// <summary>
/// Thread-safe per-repo last-known-good holder. Written only by a single
/// enrichment worker; read by <c>DashboardRefreshService</c> when it composes a
/// snapshot. Keeps the snapshot single-writer: enrichment values live here, not
/// in <c>DashboardSnapshotState</c>. Last-known-good is implicit — a failed sweep
/// is never written, so the prior value survives. Returns <c>default</c> for an
/// unknown repo; list-typed consumers coalesce to an empty list at the read site.
/// When constructed with a <see cref="Duration"/> max-age, entries older than
/// that window are treated as cache misses so a transient fetch error cannot
/// strand a stale signal beyond one TTL window.
/// </summary>
public sealed class PerRepoCache<T> where T : class
{
    private readonly record struct CacheEntry(T Value, Instant WrittenAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _byRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly IClock? _clock;
    private readonly Duration? _maxAge;

    public PerRepoCache() { }

    public PerRepoCache(IClock clock, Duration maxAge)
    {
        _clock = clock;
        _maxAge = maxAge;
    }

    public bool IsEmpty => _byRepo.IsEmpty;

    public void Update(string repo, T value) =>
        _byRepo[repo] = new CacheEntry(value, _clock?.GetCurrentInstant() ?? Instant.MinValue);

    public bool TryGet(string repo, out T? value)
    {
        if (!_byRepo.TryGetValue(repo, out var entry))
        {
            value = null;
            return false;
        }
        if (_maxAge is { } age && _clock!.GetCurrentInstant() - entry.WrittenAt > age)
        {
            value = null;
            return false;
        }
        value = entry.Value;
        return true;
    }
}
