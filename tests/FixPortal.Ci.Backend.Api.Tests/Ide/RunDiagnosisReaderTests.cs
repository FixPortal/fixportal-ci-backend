using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
        using var content = Content(Zip(("job.txt", Encoding.UTF8.GetBytes("hello"))));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Excerpt.Should().Be("hello");
        result.Truncated.Should().BeFalse();
        result.TextSha256.Should().Be("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
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

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Nul_containing_entry_names_are_rejected()
    {
        var archive = Zip(("x.txt", "log"u8.ToArray()));
        ReplaceEntryName(archive, "x.txt"u8);
        using var content = Content(archive);

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task More_than_128_entries_are_rejected()
    {
        using var content = Content(
            Zip(Enumerable.Range(0, 129).Select(index => ($"{index}.txt", Array.Empty<byte>())).ToArray())
        );

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Body_limit_is_enforced_from_reads_without_content_length()
    {
        using var content = new UnknownLengthContent(new byte[16 * 1024 * 1024 + 1]);
        content.Headers.ContentLength.Should().BeNull();

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Expanded_limit_is_enforced_from_reads_despite_misleading_metadata()
    {
        var archive = ZipExpanded(32 * 1024 * 1024 + 1);
        using var content = Content(archive);

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Expanded_metadata_is_not_used_as_the_byte_limit()
    {
        var archive = Zip(("job.txt", "hello"u8.ToArray()));
        SetCentralDirectoryUncompressedSize(archive, 32 * 1024 * 1024 + 1);
        using var content = Content(archive);

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Excerpt.Should().Be("hello");
    }

    [Theory]
    [InlineData("nested.zip")]
    [InlineData("binary.bin")]
    public async Task Nested_archives_and_unsupported_entries_are_rejected(string name)
    {
        using var content = Content(Zip((name, "content"u8.ToArray())));

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Encrypted_entries_are_rejected()
    {
        var archive = Zip(("job.txt", "content"u8.ToArray()));
        SetEncryptionFlags(archive);
        using var content = Content(archive);

        var act = async () => await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Invalid_utf8_is_replaced()
    {
        using var content = Content(Zip(("job.txt", [0xff])));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Excerpt.Should().Be("\ufffd");
        result.TextSha256.Should().Be("sha256:83d544ccc223c057d2bf80d3f2a32982c32c3c0db8e2674820da5064783fb097");
    }

    [Fact]
    public async Task Text_is_truncated_at_a_valid_utf8_boundary()
    {
        var text = new string('a', 512 * 1024 - 1) + "€tail";
        using var content = Content(Zip(("job.txt", Encoding.UTF8.GetBytes(text))));

        var result = await RunDiagnosisReader.ReadArchiveAsync(content, TestContext.Current.CancellationToken);

        result.Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Excerpt).Should().Be(512 * 1024 - 1);
        result.Excerpt.Should().Be(new string('a', 512 * 1024 - 1));
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
