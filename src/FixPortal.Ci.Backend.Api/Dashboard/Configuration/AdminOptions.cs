namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

// Properties are populated by Microsoft.Extensions.Configuration binding.
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

public sealed class AdminOptions
{
    public string AdminKey { get; init; } = "";
    public bool ExposePrivateToGuests { get; init; }
}
