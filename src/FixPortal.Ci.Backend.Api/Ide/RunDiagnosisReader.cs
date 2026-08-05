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

    public async Task<RunDiagnosisReadResult> ReadAsync(string repository, long runId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProviderTimeout);

        try
        {
            using var first = await gitHub.DownloadRunLogsAsync(httpClient, repository, runId, timeout.Token);
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

        using var archive = new ZipArchive(body, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }

        var excerpt = new StringBuilder();
        var excerptBytes = 0;
        var excerptClosed = false;
        long textBytes = 0;
        long expandedBytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);

            using var expanded = new MemoryStream();
            await using (var input = await entry.OpenAsync(ct))
            {
                expandedBytes += await CopyBoundedAsync(input, expanded, MaximumExpandedBytes - expandedBytes, ct);
            }

            var text = Encoding.UTF8.GetString(expanded.GetBuffer(), 0, checked((int)expanded.Length));
            AppendUtf8(hash, text);
            textBytes += Encoding.UTF8.GetByteCount(text);
            AppendExcerpt(excerpt, ref excerptBytes, ref excerptClosed, text);
        }

        return new(
            $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}",
            textBytes > excerptBytes,
            excerpt.ToString()
        );
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long remaining, CancellationToken ct)
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
                    return copied;
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
        if (count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Invalid diagnosis archive.");
        }
        var offset = checked((int)BitConverter.ToUInt32(bytes[(end + 16)..]));
        for (var entry = 0; entry < count; entry++)
        {
            if (offset > bytes.Length - 46 || BitConverter.ToUInt32(bytes[offset..]) != centralSignature)
            {
                throw new InvalidDataException("Invalid diagnosis archive.");
            }
            if ((BitConverter.ToUInt16(bytes[(offset + 8)..]) & 1) != 0)
            {
                throw new InvalidDataException("Encrypted diagnosis entries are unsupported.");
            }
            offset = checked(
                offset
                + 46
                + BitConverter.ToUInt16(bytes[(offset + 28)..])
                + BitConverter.ToUInt16(bytes[(offset + 30)..])
                + BitConverter.ToUInt16(bytes[(offset + 32)..])
            );
        }
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
