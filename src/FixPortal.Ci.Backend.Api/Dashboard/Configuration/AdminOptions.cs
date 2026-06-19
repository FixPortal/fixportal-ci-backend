namespace FixPortal.Ci.Backend.Api.Dashboard.Configuration;

public sealed class AdminOptions
{
    public string AdminKey { get; init; } = "";
    public bool ExposePrivateToGuests { get; init; } = false;
}
