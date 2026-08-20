using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;

namespace FixPortal.Ci.Backend.Api.Ide;

internal enum RunDiagnosisReadStatus
{
    Available,
    Unavailable,
    TimedOut,
    Rejected,
}

internal sealed record RunDiagnosisContent(string TextSha256, bool Truncated, string Excerpt);

internal sealed record RunDiagnosisReadResult(RunDiagnosisReadStatus Status, RunDiagnosisContent? Content = null);

internal sealed class RunDiagnosisReader(HttpClient httpClient, GitHubOrgClient gitHub)
{
    private const int MaximumBodyBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 128;
    private const int MaximumExpandedBytes = 32 * 1024 * 1024;
    private const int MaximumTextBytes = 512 * 1024;
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15);

    public async Task<RunDiagnosisReadResult> ReadAsync(
        string repository,
        long runId,
        int attempt,
        CancellationToken ct
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);

        try
        {
            using var first = await gitHub.DownloadRunLogsAsync(httpClient, repository, runId, attempt, timeout.Token);
            if (first.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new(RunDiagnosisReadStatus.Unavailable);
            }
            if (
                first.StatusCode != HttpStatusCode.Found
                || first.Headers.Location is not { IsAbsoluteUri: true } location
                || location.Scheme != Uri.UriSchemeHttps
            )
            {
                return new(RunDiagnosisReadStatus.Rejected);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new(RunDiagnosisReadStatus.Unavailable);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new(RunDiagnosisReadStatus.Rejected);
            }

            return new(RunDiagnosisReadStatus.Available, await ReadArchiveAsync(response.Content, timeout.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(RunDiagnosisReadStatus.TimedOut);
        }
        catch (Exception ex)
            when (ex
                    is GitHubAuthException
                        or GitHubRateLimitException
                        or HttpRequestException
                        or IOException
                        or InvalidDataException
                        or NotSupportedException
            )
        {
            return new(RunDiagnosisReadStatus.Rejected);
        }
    }

    internal static async Task<RunDiagnosisContent> ReadArchiveAsync(HttpContent content, CancellationToken ct)
    {
        using var body = new MemoryStream();
        await CopyBoundedAsync(await content.ReadAsStreamAsync(ct), body, MaximumBodyBytes, ct);
        RejectEncryptedEntries(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        body.Position = 0;

        await using var archive = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: false);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        if (files.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }

        var excerpt = new StringBuilder();
        var excerptBytes = 0;
        var excerptClosed = false;
        long textBytes = 0;
        long expandedBytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));

        try
        {
            foreach (var entry in files)
            {
                ValidateEntryName(entry.FullName);
                // Delimit each entry: run-log zips are split per job/step, so without a
                // header unrelated logs merge into one run-on stream and an excerpted
                // stack trace can be attributed to the wrong step. The header is part of
                // the hashed text and the excerpt, so TextSha256 authenticates it too.
                var header = $"--- {entry.FullName} ---\n";
                AppendUtf8(hash, header);
                textBytes += Encoding.UTF8.GetByteCount(header);
                AppendExcerpt(excerpt, ref excerptBytes, ref excerptClosed, header);
                var read = await ReadEntryAsync(
                    entry,
                    MaximumExpandedBytes - expandedBytes,
                    hash,
                    excerpt,
                    excerptBytes,
                    excerptClosed,
                    bytes,
                    chars,
                    ct
                );
                expandedBytes += read.ExpandedBytes;
                textBytes += read.TextBytes;
                excerptBytes = read.ExcerptBytes;
                excerptClosed = read.ExcerptClosed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }

        return new(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}",
            textBytes > excerptBytes,
            excerpt.ToString()
        );
    }

    private static async Task<(
        long ExpandedBytes,
        long TextBytes,
        int ExcerptBytes,
        bool ExcerptClosed
    )> ReadEntryAsync(
        ZipArchiveEntry entry,
        long remainingExpandedBytes,
        IncrementalHash hash,
        StringBuilder excerpt,
        int excerptBytes,
        bool excerptClosed,
        byte[] bytes,
        char[] chars,
        CancellationToken ct
    )
    {
        long expandedBytes = 0;
        long textBytes = 0;
        var decoder = Encoding.UTF8.GetDecoder();
        await using var input = await entry.OpenAsync(ct);

        while (true)
        {
            var read = await input.ReadAsync(bytes.AsMemory(0, bytes.Length), ct);
            expandedBytes += read;
            if (expandedBytes > remainingExpandedBytes)
            {
                throw new InvalidDataException("Diagnosis content exceeds its limit.");
            }

            decoder.Convert(
                bytes.AsSpan(0, read),
                chars,
                flush: read == 0,
                out var bytesUsed,
                out var charsUsed,
                out _
            );
            if (bytesUsed != read)
            {
                throw new InvalidDataException("Invalid diagnosis text.");
            }
            if (charsUsed > 0)
            {
                var text = new string(chars, 0, charsUsed);
                AppendUtf8(hash, text);
                textBytes += Encoding.UTF8.GetByteCount(text);
                AppendExcerpt(excerpt, ref excerptBytes, ref excerptClosed, text);
            }
            if (read == 0)
            {
                return (expandedBytes, textBytes, excerptBytes, excerptClosed);
            }
        }
    }

    // Returns nothing: the copied count is only ever needed for the bound check below, and
    // no caller reads it.
    private static async Task CopyBoundedAsync(Stream input, Stream output, long remaining, CancellationToken ct)
    {
        if (remaining < 0)
        {
            throw new InvalidDataException("Diagnosis content exceeds its limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    return;
                }
                if (copied + read > remaining)
                {
                    throw new InvalidDataException("Diagnosis content exceeds its limit.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateEntryName(string name)
    {
        var normalized = name.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (
            string.IsNullOrEmpty(name)
            || name.Contains('\0')
            || normalized.StartsWith('/')
            || name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'
            || parts.Any(part => part is "" or "." or "..")
            || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException("Invalid diagnosis archive entry.");
        }
    }

    private static void RejectEncryptedEntries(ReadOnlySpan<byte> bytes)
    {
        const uint endSignature = 0x06054b50;
        const uint centralSignature = 0x02014b50;
        var minimum = Math.Max(0, bytes.Length - ushort.MaxValue - 22);
        var end = -1;
        for (var index = bytes.Length - 22; index >= minimum; index--)
        {
            if (BitConverter.ToUInt32(bytes[index..]) == endSignature)
            {
                end = index;
                break;
            }
        }
        if (end < 0)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }

        var count = BitConverter.ToUInt16(bytes[(end + 10)..]);
        if (count == 0)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }
        // The 128-entry cap in ReadArchiveAsync counts only FILE entries, after
        // ZipArchive has materialised every central-directory record — including
        // directory entries. Bound the record count here, before the archive is opened,
        // so a directory-entry-heavy archive cannot amplify allocation past the cap.
        if (count > MaximumEntries * 2)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }
        var offset = ReadOffset(bytes, end + 16);
        for (var entry = 0; entry < count; entry++)
        {
            if (offset > bytes.Length - 46 || BitConverter.ToUInt32(bytes[offset..]) != centralSignature)
            {
                throw new InvalidDataException("Invalid diagnosis archive.");
            }
            RejectEncryptedEntry(bytes, offset);
            offset = checked(
                offset
                + 46
                + BitConverter.ToUInt16(bytes[(offset + 28)..])
                + BitConverter.ToUInt16(bytes[(offset + 30)..])
                + BitConverter.ToUInt16(bytes[(offset + 32)..])
            );
        }
    }

    private static void RejectEncryptedEntry(ReadOnlySpan<byte> bytes, int centralOffset)
    {
        const uint localSignature = 0x04034b50;
        var localOffset = ReadOffset(bytes, centralOffset + 42);
        if (localOffset > bytes.Length - 30 || BitConverter.ToUInt32(bytes[localOffset..]) != localSignature)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }
        if (
            (BitConverter.ToUInt16(bytes[(centralOffset + 8)..]) & 1) != 0
            || (BitConverter.ToUInt16(bytes[(localOffset + 6)..]) & 1) != 0
        )
        {
            throw new InvalidDataException("Encrypted diagnosis entries are unsupported.");
        }
    }

    private static int ReadOffset(ReadOnlySpan<byte> bytes, int position)
    {
        var value = BitConverter.ToUInt32(bytes[position..]);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }
        return (int)value;
    }

    private static void AppendUtf8(IncrementalHash hash, string text)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var encoder = Encoding.UTF8.GetEncoder();
            var consumed = 0;
            while (consumed < text.Length)
            {
                encoder.Convert(
                    text.AsSpan(consumed),
                    buffer,
                    flush: true,
                    out var charsUsed,
                    out var bytesUsed,
                    out _
                );
                hash.AppendData(buffer, 0, bytesUsed);
                consumed += charsUsed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendExcerpt(StringBuilder excerpt, ref int excerptBytes, ref bool excerptClosed, string text)
    {
        if (excerptClosed || excerptBytes == MaximumTextBytes)
        {
            excerptClosed = true;
            return;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > MaximumTextBytes - excerptBytes)
            {
                excerptClosed = true;
                return;
            }
            _ = excerpt.Append(rune);
            excerptBytes += rune.Utf8SequenceLength;
        }
    }
}
