using System.Text.Json.Serialization;

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

public sealed class GitHubOptions
{
    public required string Owner { get; init; }

    // [JsonIgnore] keeps the token out of any System.Text.Json serialization of
    // the bound options object (e.g. structured logging); config binding is
    // unaffected. ToString() is overridden so the token never lands in a log line.
    [JsonIgnore]
    public required string Token { get; init; }

    public override string ToString() => $"GitHubOptions {{ Owner = {Owner}, Token = *** }}";
}
