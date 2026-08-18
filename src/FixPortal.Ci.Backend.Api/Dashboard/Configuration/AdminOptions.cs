namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

public sealed class AdminOptions
{
    public string AdminKey { get; init; } = "";
    public bool ExposePrivateToGuests { get; init; }

    // Null-tolerant: an explicitly null bound value must produce the configured
    // validation message, not a NullReferenceException from the validator itself.
    public bool HasValidAdminKeyLength() => AdminKey is null or { Length: 0 } || AdminKey.Length >= 16;
}
