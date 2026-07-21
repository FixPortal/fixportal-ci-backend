using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace FixPortal.Ci.Backend.Api.Dashboard.Services;

public sealed class FileDashboardSnapshotStore(string snapshotPath) : IDashboardSnapshotStore
{
    // The snapshot carries NodaTime Instants, which default System.Text.Json
    // cannot round-trip. Configure the serializer for NodaTime at this I/O
    // boundary so persisted snapshots reload faithfully.
    private static readonly JsonSerializerOptions SerializerOptions =
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public async Task<DashboardSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        // Open directly rather than checking File.Exists first — the TOCTOU window
        // between the check and the open would mask a concurrent SaveAsync swap.
        FileStream stream;
        try
        {
            stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            var snapshot = await JsonSerializer.DeserializeAsync<DashboardSnapshot>(
                stream, SerializerOptions, cancellationToken);

            // Trend buckets have been clock-hour-anchored since the CI-trend rewrite.
            // A snapshot from before that change carries buckets anchored to an
            // arbitrary refresh instant, which no longer key-match in MergeTrends — so
            // a degraded first refresh after deploy would silently drop the history.
            // Discard such a stale-format snapshot and rebuild from fresh data instead.
            if (snapshot?.CiTrend is { Count: > 0 } trend
                && trend[0].BucketStart.ToUnixTimeSeconds() % 3600 != 0)
            {
                return null;
            }

            return snapshot;
        }
    }

    public async Task SaveAsync(DashboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        // Write to a temp file, flush to disk, then move into place. The move
        // makes the swap atomic against torn *application* writes; the flush
        // narrows (does not eliminate) the power-loss window. On any failure the
        // temp file is removed so it cannot be promoted or left to accumulate.
        var tempPath = snapshotPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                // Synchronous by design: FileStream has no async overload that
                // forces an fsync (flushToDisk). This is a cold persistence path,
                // not a request thread, so the brief block is acceptable; the
                // fsync itself is not cancellable once issued.
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, snapshotPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            /* Best-effort cleanup; a leftover temp file is overwritten next save. */
        }
    }
}
