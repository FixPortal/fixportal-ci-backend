using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FixPortal.Ci.Backend.Api.Integrations.GitHub;

/// <summary>
/// Supplies the bearer token for GitHub calls. Exists so the client does not care whether
/// it is authenticating with a personal access token or as a GitHub App installation —
/// the App path needs an async, expiring, refreshable token, and a static PAT does not.
/// </summary>
public interface IGitHubTokenSource
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>The configured personal access token, unchanged for the process lifetime.</summary>
public sealed class StaticGitHubTokenSource(IOptions<GitHubOptions> gitHub) : IGitHubTokenSource
{
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult(gitHub.Value.Token);
}

/// <summary>
/// Mints installation access tokens for a GitHub App: sign a short-lived JWT with the
/// App's private key, exchange it for an installation token, cache that until shortly
/// before it expires.
/// </summary>
/// <remarks>
/// Installation tokens last an hour. They are refreshed early rather than on failure,
/// because discovering expiry by 401 would surface as an auth error on an unrelated call
/// and, worse, would trip the health state that a real credential problem is supposed to
/// signal.
/// </remarks>
public sealed class GitHubAppTokenSource(
    HttpClient httpClient,
    IOptions<GitHubAppOptions> appOptions,
    IOptions<GitHubOptions> gitHub,
    IClock clock,
    ILogger<GitHubAppTokenSource> logger
) : IGitHubTokenSource
{
    // Refresh with time to spare: a token that expires mid-sweep would fail a request
    // that had already been paid for in rate budget.
    private static readonly Duration RefreshMargin = Duration.FromMinutes(5);

    // GitHub rejects a JWT whose exp is more than 10 minutes out, and clock skew between
    // us and GitHub is real, so this is deliberately short and iat is backdated.
    private static readonly Duration JwtLifetime = Duration.FromMinutes(9);
    private static readonly Duration JwtBackdate = Duration.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private Instant _expiresAt = Instant.MinValue;
    private string? _installationId;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is { Length: > 0 } cached && clock.GetCurrentInstant() + RefreshMargin < _expiresAt)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: several sweeps can arrive at an expiry together,
            // and minting one token each would be wasteful and needlessly rate-limited.
            if (_token is { Length: > 0 } fresh && clock.GetCurrentInstant() + RefreshMargin < _expiresAt)
            {
                return fresh;
            }

            var jwt = CreateJwt();
            _installationId ??= await ResolveInstallationIdAsync(jwt, ct);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"app/installations/{_installationId}/access_tokens"
            );
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            AddAppHeaders(request);

            using var response = await httpClient.SendAsync(request, ct);
            _ = response.EnsureSuccessStatusCode();

            var minted = await response.Content.ReadFromJsonAsync<InstallationTokenResponse>(ct);
            if (minted?.Token is not { Length: > 0 })
            {
                throw new InvalidOperationException("GitHub returned no installation token.");
            }

            _token = minted.Token;
            _expiresAt = minted.ExpiresAt ?? clock.GetCurrentInstant() + Duration.FromMinutes(60);
            logger.LogInformation(
                "Minted a GitHub App installation token for installation {Installation}, valid until {ExpiresAt}.",
                _installationId,
                _expiresAt
            );
            return _token;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<string> ResolveInstallationIdAsync(string jwt, CancellationToken ct)
    {
        if (appOptions.Value.InstallationId is { Length: > 0 } configured)
        {
            return configured;
        }

        // Discovered rather than configured so reinstalling the App does not require a
        // redeploy — the installation id changes, the owner does not.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"orgs/{gitHub.Value.Owner}/installation");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        AddAppHeaders(request);

        using var response = await httpClient.SendAsync(request, ct);
        _ = response.EnsureSuccessStatusCode();

        var installation = await response.Content.ReadFromJsonAsync<InstallationResponse>(ct);
        return installation?.Id?.ToString()
            ?? throw new InvalidOperationException(
                $"The GitHub App is not installed on {gitHub.Value.Owner}, or the App cannot see the installation."
            );
    }

    private static void AddAppHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "fixportal-ci-backend");
        request.Headers.Add("Accept", "application/vnd.github+json");
    }

    /// <summary>
    /// RS256 JWT, hand-rolled rather than pulling in a JWT library for one 3-field token.
    /// internal so the encoding is testable without a live App.
    /// </summary>
    internal string CreateJwt()
    {
        var options = appOptions.Value;
        var now = clock.GetCurrentInstant();

        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    // Backdated: GitHub rejects a JWT issued in its future, and a second
                    // or two of clock skew is normal.
                    iat = (now - JwtBackdate).ToUnixTimeSeconds(),
                    exp = (now + JwtLifetime).ToUnixTimeSeconds(),
                    iss = options.AppId,
                }
            )
        );

        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalisePem(options.PrivateKeyPem!));
        var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    /// <summary>
    /// Restores real newlines to a PEM that has been flattened to literal "\n", which is
    /// what happens whenever a multi-line secret travels through an environment variable
    /// or a deployment parameter. ImportFromPem rejects the flattened form outright.
    /// </summary>
    internal static string NormalisePem(string pem) => pem.Replace("\\n", "\n").Replace("\r\n", "\n").Trim();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record InstallationTokenResponse(string? Token, Instant? ExpiresAt);

    private sealed record InstallationResponse(long? Id);
}
