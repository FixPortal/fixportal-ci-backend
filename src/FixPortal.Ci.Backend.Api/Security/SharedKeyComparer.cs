using System.Security.Cryptography;
using System.Text;

namespace FixPortal.Ci.Backend.Api.Security;

internal static class SharedKeyComparer
{
    // Hash both sides first so the compared buffers are always 32 bytes. FixedTimeEquals
    // returns early when the lengths differ, so comparing the raw bytes would reveal the
    // configured key's length through response timing.
    internal static bool Matches(string? provided, string? configured) =>
        !string.IsNullOrEmpty(configured)
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided ?? "")),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured))
        );
}
