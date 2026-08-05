namespace FixPortal.Ci.Backend.Api.Ide;

public sealed class IdeIntegrationOptions
{
    public string ApiKey { get; init; } = "";

    public bool IsValid(string? adminKey) =>
        string.IsNullOrEmpty(ApiKey)
        || ApiKey.Length >= 32
            && ApiKey == ApiKey.Trim()
            && !ApiKey.Contains("{{", StringComparison.Ordinal)
            && !ApiKey.Contains("${", StringComparison.Ordinal)
            && !ApiKey.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ApiKey, adminKey, StringComparison.Ordinal);
}
