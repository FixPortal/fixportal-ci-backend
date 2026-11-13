using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Security;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Security;

public class SharedKeyComparerTests
{
    [Theory]
    [InlineData("correct-horse-battery", "correct-horse-battery", true)]
    [InlineData("correct-horse-batterx", "correct-horse-battery", false)]
    [InlineData("short", "correct-horse-battery", false)]
    [InlineData("correct-horse-battery-staple", "correct-horse-battery", false)]
    [InlineData("", "correct-horse-battery", false)]
    [InlineData(null, "correct-horse-battery", false)]
    [InlineData("correct-horse-battery", "", false)]
    [InlineData("correct-horse-battery", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void Matches_accepts_only_an_identical_non_empty_key(string? provided, string? configured, bool expected) =>
        SharedKeyComparer.Matches(provided, configured).Should().Be(expected);
}
