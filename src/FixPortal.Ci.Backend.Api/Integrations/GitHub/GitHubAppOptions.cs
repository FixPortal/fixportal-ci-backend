using System.Text.Json.Serialization;

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

/// <summary>
/// GitHub App credentials. When these are configured the dashboard authenticates as an
/// App installation instead of with a personal access token.
/// </summary>
/// <remarks>
/// <para>
/// Two reasons, both learned the hard way. First, a fine-grained PAT cannot read check
/// runs: <c>statusCheckRollup</c> returns "Resource not accessible by personal access
/// token" per CheckRun node, there is no "Checks" permission to grant in the PAT UI, and
/// because GitHub fails an aliased GraphQL document as a whole, that one unreadable field
/// took every review pill down with it.
/// </para>
/// <para>
/// Second, GraphQL's 5,000-points/hour budget is metered per USER, so a PAT-authenticated
/// dashboard competes with whoever is running <c>gh</c> at a terminal — which is how a
/// runaway sweep came to block an unrelated deploy. An installation gets its own budget,
/// so the two can no longer starve each other.
/// </para>
/// </remarks>
public sealed class GitHubAppOptions
{
    /// <summary>Numeric App ID from the App's settings page.</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Installation to mint tokens for. Optional: left unset, it is discovered once via
    /// <c>GET /orgs/{owner}/installation</c>, so a reinstall does not need a config change.
    /// </summary>
    public string? InstallationId { get; init; }

    /// <summary>
    /// PEM-encoded RSA private key. Accepts either real newlines or literal "\n"
    /// sequences, because passing a multi-line value through an environment variable or a
    /// deployment parameter routinely flattens it to the latter.
    /// </summary>
    [JsonIgnore]
    public string? PrivateKeyPem { get; init; }

    /// <summary>True when enough is configured to authenticate as an App.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(PrivateKeyPem);

    public override string ToString() =>
        $"GitHubAppOptions {{ AppId = {AppId}, InstallationId = {InstallationId}, PrivateKeyPem = *** }}";
}
