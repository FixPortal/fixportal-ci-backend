using AwesomeAssertions;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests;

public class OwnedTempDirectoryTests
{
    [Fact]
    public void Dispose_should_delete_the_directory_and_its_contents_when_the_witness_is_intact()
    {
        var fixture = new OwnedTempDirectory(Path.GetTempPath(), "owned-temp-test-");
        var root = fixture.Root;
        Directory.CreateDirectory(Path.Join(root, "nested"));
        File.WriteAllText(Path.Join(root, "nested", "file.txt"), "x");

        fixture.Dispose();

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public void Dispose_should_refuse_to_delete_a_directory_whose_witness_has_been_replaced()
    {
        var fixture = new OwnedTempDirectory(Path.GetTempPath(), "owned-temp-test-");
        var root = fixture.Root;
        File.WriteAllText(Path.Join(root, ".fixture-owner"), "someone-else");
        File.WriteAllText(Path.Join(root, "keep.txt"), "x");

        fixture.Dispose();

        try
        {
            File.Exists(Path.Join(root, "keep.txt")).Should().BeTrue("an unvalidated target must not be deleted");
        }
        finally
        {
            // Test-owned: just created above under the temp directory; restore the witness so it is deleted.
            File.WriteAllText(Path.Join(root, ".fixture-owner"), Path.GetFileName(root)[^32..]);
            fixture.Dispose();
        }
    }

    [Fact]
    public void Dispose_should_not_throw_when_the_directory_is_already_gone()
    {
        var fixture = new OwnedTempDirectory(Path.GetTempPath(), "owned-temp-test-");
        Directory.Delete(fixture.Root, recursive: true);

        var act = fixture.Dispose;

        act.Should().NotThrow();
    }
}
