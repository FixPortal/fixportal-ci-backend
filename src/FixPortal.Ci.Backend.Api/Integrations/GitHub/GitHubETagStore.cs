using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

// Per-URL ETag validator paired with the payload decoded from the last 200, so the
// GitHub client can issue conditional GETs (If-None-Match). GitHub does NOT charge a
// 304 Not Modified against the primary REST rate limit, so a stable repository costs
// nothing per refresh cycle — this is what lets the dashboard poll on a tight (20s)
// cadence without exhausting the 5000/hr PAT budget. Registered as a singleton so the
// cache outlives the transient typed-client instances and persists across cycles.
public sealed class GitHubETagStore : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2000 });

    public sealed record Entry(EntityTagHeaderValue ETag, object? Payload);

    public Entry? Get(string url) => _cache.TryGetValue(url, out Entry? entry) ? entry : null;

    public void Set(string url, EntityTagHeaderValue etag, object? payload)
    {
        var entry = new Entry(etag, payload);
        var options = new MemoryCacheEntryOptions { Size = 1 };
        _ = _cache.Set(url, entry, options);
    }

    public void Dispose() => _cache.Dispose();
}
