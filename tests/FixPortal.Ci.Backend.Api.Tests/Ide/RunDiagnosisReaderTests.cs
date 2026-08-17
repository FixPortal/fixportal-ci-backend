using System.IO.Compression;
using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Ide;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Ide;

public class RunDiagnosisReaderTests
{
    [Fact]
    public async Task Text_entries_are_accepted_and_hash_is_stable()
    {
        using var content = Content(Zip(("job.txt", "hello"u8.ToArray())));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        // Entries are delimited with a filename header so multi-job archives cannot
        // merge into one run-on stream; the header is part of the hashed text.
        result.Excerpt.Should().Be("--- job.txt ---\nhello");
        result.Truncated.Should().BeFalse();
        result.TextSha256.Should().Be("sha256:fe5243bc6b2f78e2a8f5ab409622e639f0382a80d19fd2850ce43fc880b5ca24");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("safe/../../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("\\absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    [InlineData("C:drive-relative.txt")]
    public async Task Unsafe_entry_paths_are_rejected(string name)
    {
        using var content = Content(Zip((name, "log"u8.ToArray())));

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Nul_containing_entry_names_are_rejected()
    {
        var archive = Zip(("x.txt", "log"u8.ToArray()));
        ReplaceEntryName(archive, "x.txt"u8);
        using var content = Content(archive);

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task More_than_128_entries_are_rejected()
    {
        using var content = Content(
            Zip(Enumerable.Range(0, 129).Select(index => ($"{index}.txt", Array.Empty<byte>())).ToArray())
        );

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Directory_entries_are_ignored_and_do_not_count_toward_the_file_limit()
    {
        var entries = Enumerable
            .Range(0, 128)
            .Select(index => ($"logs/{index}.txt", Array.Empty<byte>()))
            .Prepend(("logs/", Array.Empty<byte>()))
            .ToArray();
        using var content = Content(Zip(entries));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        // No entry text, but each file still contributes its delimiter header.
        result.Excerpt.Should().Be(string.Join("", Enumerable.Range(0, 128).Select(i => $"--- logs/{i}.txt ---\n")));
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Directory_entries_do_not_relax_the_128_file_limit()
    {
        var entries = Enumerable
            .Range(0, 129)
            .Select(index => ($"logs/{index}.txt", Array.Empty<byte>()))
            .Prepend(("logs/", Array.Empty<byte>()))
            .ToArray();
        using var content = Content(Zip(entries));

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Body_limit_is_enforced_from_reads_without_content_length()
    {
        using var content = new UnknownLengthContent(new byte[16 * 1024 * 1024 + 1]);
        content.Headers.ContentLength.Should().BeNull();

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Expanded_limit_is_enforced_from_reads_despite_misleading_metadata()
    {
        var archive = ZipExpanded(32 * 1024 * 1024 + 1);
        using var content = Content(archive);

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Expanded_metadata_is_not_used_as_the_byte_limit()
    {
        var archive = Zip(("job.txt", "hello"u8.ToArray()));
        SetCentralDirectoryUncompressedSize(archive, 32 * 1024 * 1024 + 1);
        using var content = Content(archive);

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Excerpt.Should().Be("--- job.txt ---\nhello");
    }

    [Theory]
    [InlineData("nested.zip")]
    [InlineData("binary.bin")]
    public async Task Nested_archives_and_unsupported_entries_are_rejected(string name)
    {
        using var content = Content(Zip((name, "content"u8.ToArray())));

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Encrypted_entries_are_rejected()
    {
        var archive = Zip(("job.txt", "content"u8.ToArray()));
        SetEncryptionFlags(archive);
        using var content = Content(archive);

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("oversized central offset")]
    [InlineData("local encryption flag")]
    public async Task Malformed_zip_headers_are_rejected_as_invalid_data(string corruption)
    {
        var archive = Zip(("job.txt", "content"u8.ToArray()));
        CorruptHeader(archive, corruption);
        using var content = Content(archive);

        var read = RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);
        var act = async () => await read;

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Invalid_utf8_is_replaced()
    {
        using var content = Content(Zip(("job.txt", [0xff])));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Excerpt.Should().Be("--- job.txt ---\n\ufffd");
        result.TextSha256.Should().Be("sha256:e52eab1eec7650ba43e1a07654574fa6c8e3bfa2113b048b74d2a8ff4db2bbf7");
    }

    [Fact]
    public async Task Text_is_truncated_at_a_valid_utf8_boundary()
    {
        var text = new string('a', 512 * 1024 - 1) + "€tail";
        using var content = Content(Zip(("job.txt", Encoding.UTF8.GetBytes(text))));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Excerpt).Should().Be(512 * 1024);
        // The 16-byte entry header spends part of the 512 KB excerpt budget first.
        result.Excerpt.Should().Be("--- job.txt ---\n" + new string('a', 512 * 1024 - 16));
    }

    [Fact]
    public async Task Utf8_sequences_split_across_reader_chunks_preserve_text_and_hash()
    {
        var text = new string('a', 64 * 1024 - 1) + "💡tail";
        using var content = Content(Zip(("job.txt", Encoding.UTF8.GetBytes(text))));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        var expected = "--- job.txt ---\n" + text;
        result.Excerpt.Should().Be(expected);
        result
            .TextSha256.Should()
            .Be(
                $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant()}"
            );
    }

    private static StreamContent Content(byte[] bytes) => new(new MemoryStream(bytes));

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var output = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                output.Write(bytes);
            }
        }
        return stream.ToArray();
    }

    private static byte[] ZipExpanded(int size)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var output = archive.CreateEntry("large.txt", CompressionLevel.SmallestSize).Open())
        {
            var block = new byte[64 * 1024];
            for (var remaining = size; remaining > 0; remaining -= Math.Min(remaining, block.Length))
            {
                output.Write(block, 0, Math.Min(remaining, block.Length));
            }
        }
        return stream.ToArray();
    }

    private static void SetCentralDirectoryUncompressedSize(byte[] archive, uint value)
    {
        var offset = FindSignature(archive, 0x02014b50);
        BitConverter.GetBytes(value).CopyTo(archive, offset + 24);
    }

    private static void SetEncryptionFlags(byte[] archive)
    {
        var local = FindSignature(archive, 0x04034b50);
        var central = FindSignature(archive, 0x02014b50);
        archive[local + 6] |= 1;
        archive[central + 8] |= 1;
    }

    private static void CorruptHeader(byte[] archive, string corruption)
    {
        if (corruption == "oversized central offset")
        {
            BitConverter.GetBytes(uint.MaxValue).CopyTo(archive, FindSignature(archive, 0x06054b50) + 16);
            return;
        }

        archive[FindSignature(archive, 0x04034b50) + 6] |= 1;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        var expected = BitConverter.GetBytes(signature);
        for (var index = 0; index <= bytes.Length - expected.Length; index++)
        {
            if (bytes.AsSpan(index, expected.Length).SequenceEqual(expected))
            {
                return index;
            }
        }
        throw new InvalidOperationException("ZIP signature not found.");
    }

    private static void ReplaceEntryName(byte[] bytes, ReadOnlySpan<byte> name)
    {
        for (var index = 0; index <= bytes.Length - name.Length; index++)
        {
            if (bytes.AsSpan(index, name.Length).SequenceEqual(name))
            {
                bytes[index] = 0;
            }
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
