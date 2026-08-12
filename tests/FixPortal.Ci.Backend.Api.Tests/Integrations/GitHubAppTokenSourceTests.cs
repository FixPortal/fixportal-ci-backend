using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

// The App is how the dashboard reads check runs at all: a fine-grained PAT is refused on
// statusCheckRollup and there is no "Checks" permission to grant it. It also moves the
// dashboard onto its own GraphQL points budget, so a sweep can no longer starve a human
// running gh. Both make the JWT worth pinning - a malformed one fails as an opaque 401.
public sealed class GitHubAppTokenSourceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 2, 20, 0, 0);
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

    private static string NewPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private GitHubAppTokenSource Create(string pem, out RSA verifier)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        verifier = rsa;

        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _httpClients.Add(http);
        return new GitHubAppTokenSource(
            http,
            Options.Create(new GitHubAppOptions { AppId = "123456", PrivateKeyPem = pem }),
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "unused" }),
            new FakeClock(Now),
            NullLogger<GitHubAppTokenSource>.Instance
        );
    }

    private static string DecodeSegment(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    [Fact]
    public void The_jwt_is_signed_with_the_app_key_and_verifies_against_its_public_half()
    {
        var pem = NewPrivateKeyPem();
        var source = Create(pem, out var verifier);

        var jwt = source.CreateJwt();

        var parts = jwt.Split('.');
        _ = parts.Should().HaveCount(3);
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Convert.FromBase64String(
            parts[2].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[2].Length % 4) % 4)
        );

        _ = verifier
            .VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should()
            .BeTrue();
        verifier.Dispose();
    }

    [Fact]
    public void The_jwt_is_backdated_and_expires_inside_githubs_ten_minute_ceiling()
    {
        var source = Create(NewPrivateKeyPem(), out var verifier);
        verifier.Dispose();

        var payload = JsonDocument.Parse(DecodeSegment(source.CreateJwt().Split('.')[1])).RootElement;

        var iat = payload.GetProperty("iat").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        var issuedAt = Instant.FromUnixTimeSeconds(iat);
        var expiresAt = Instant.FromUnixTimeSeconds(exp);

        // Backdated: GitHub rejects a JWT issued in its own future, and a second or two of
        // clock skew between us and GitHub is routine.
        _ = issuedAt.Should().BeLessThan(Now);
        // GitHub rejects anything more than 10 minutes out; the margin is deliberate.
        _ = (expiresAt - Now).Should().BeLessThan(Duration.FromMinutes(10));
        _ = (expiresAt - Now).Should().BeGreaterThan(Duration.FromMinutes(5));
        _ = payload.GetProperty("iss").GetString().Should().Be("123456");
    }

    [Theory]
    // A PEM that travelled through an environment variable or a deployment parameter
    // arrives with its newlines flattened to literal backslash-n. ImportFromPem rejects
    // that outright, and the failure reads as a bad key rather than bad plumbing.
    [InlineData("\\n")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void A_pem_survives_whatever_the_deployment_pipeline_did_to_its_newlines(string separator)
    {
        var original = NewPrivateKeyPem().Replace("\r\n", "\n").Trim();
        var mangled = original.Replace("\n", separator);

        var restored = GitHubAppTokenSource.NormalisePem(mangled);

        // The RSA key is created AND disposed inside the delegate, which NotThrow() invokes.
        // Previously the key was a `using` local in the method body and the lambda closed
        // over it, which is what AccessToDisposedClosure warns about: the delegate outlives
        // the using scope on paper, even though the assertion happens to run it first.
        // Owning the key inside the delegate removes the capture rather than suppressing it.
        var import = () =>
        {
            using var key = RSA.Create();
            key.ImportFromPem(restored);
        };
        _ = import.Should().NotThrow();
    }

    [Fact]
    public void An_unconfigured_app_is_not_treated_as_configured()
    {
        // Guards the DI switch: a half-set App must fall back to the PAT rather than
        // start up and fail every request with an unsigned JWT.
        _ = new GitHubAppOptions().IsConfigured.Should().BeFalse();
        _ = new GitHubAppOptions { AppId = "123" }
            .IsConfigured.Should()
            .BeFalse();
        _ = new GitHubAppOptions { PrivateKeyPem = "key" }
            .IsConfigured.Should()
            .BeFalse();
        _ = new GitHubAppOptions { AppId = "123", PrivateKeyPem = "key" }
            .IsConfigured.Should()
            .BeTrue();
    }

    [Fact]
    public void The_options_never_render_the_private_key()
    {
        var options = new GitHubAppOptions { AppId = "123456", PrivateKeyPem = "-----BEGIN PRIVATE KEY-----secret" };

        _ = options.ToString().Should().NotContain("secret");
        _ = options.ToString().Should().Contain("123456");
    }
}
