using System.Net;
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

    // Scripted-HTTP coverage for GetTokenAsync. The mint path was previously untested
    // end to end, which is how expires_at silently never bound (the web naming defaults
    // do not map snake_case) and auth refusals left as HttpRequestException instead of
    // GitHubAuthException.
    private sealed class MintHandler(
        string mintBody,
        HttpStatusCode mintStatus = HttpStatusCode.OK,
        string expiresAt = "2026-08-02T20:30:00Z"
    ) : HttpMessageHandler
    {
        public int InstallationLookups;
        public int Mints;

        // Settable mid-test so a reinstall scenario can flip the mint response on the
        // SAME handler — the source's installation-id cache is the thing under test.
        public HttpStatusCode? MintStatusOverride;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var (status, body) = request.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """{"id":4242}""")
                : (MintStatusOverride ?? mintStatus, mintBody.Replace("__EXPIRES_AT__", expiresAt));
            if (request.Method == HttpMethod.Get)
            {
                InstallationLookups++;
            }
            else
            {
                Mints++;
            }
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private GitHubAppTokenSource CreateMinting(MintHandler handler, FakeClock clock)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        _httpClients.Add(http);
        return new GitHubAppTokenSource(
            http,
            Options.Create(new GitHubAppOptions { AppId = "123456", PrivateKeyPem = NewPrivateKeyPem() }),
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = "unused" }),
            clock,
            NullLogger<GitHubAppTokenSource>.Instance
        );
    }

    [Fact]
    public async Task The_minted_tokens_expires_at_binds_and_drives_the_refresh()
    {
        // expires_at is 20:30 while the silent fallback would have been 21:00 (Now +
        // 60min). At 20:26 the refresh margin (5min) has entered the real expiry but
        // not the fallback one, so a second mint proves the field genuinely bound.
        var clock = new FakeClock(Now);
        var handler = new MintHandler("""{"token":"ghs_test","expires_at":"__EXPIRES_AT__"}""");
        var source = CreateMinting(handler, clock);

        _ = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        clock.Advance(Duration.FromMinutes(26));
        _ = await source.GetTokenAsync(TestContext.Current.CancellationToken);

        _ = handler.Mints.Should().Be(2, "20:26 + 5min margin is past the real 20:30 expiry but inside the 60min fallback");
    }

    [Fact]
    public async Task A_refused_mint_surfaces_as_an_auth_exception_for_the_health_signal()
    {
        var clock = new FakeClock(Now);
        var handler = new MintHandler("{}", HttpStatusCode.Unauthorized);
        var source = CreateMinting(handler, clock);

        var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

        // Only GitHubAuthException drives state.SetAuthError; a raw HttpRequestException
        // here left /api/health reporting Healthy on a dead credential.
        _ = await act.Should().ThrowAsync<GitHubAuthException>();
    }

    [Fact]
    public async Task A_failed_mint_is_negatively_cached_so_callers_do_not_thunder()
    {
        var clock = new FakeClock(Now);
        var handler = new MintHandler("{}", HttpStatusCode.Unauthorized);
        var source = CreateMinting(handler, clock);

        var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);

        _ = await act.Should().ThrowAsync<GitHubAuthException>();
        _ = await act.Should().ThrowAsync<GitHubAuthException>();

        _ = handler.Mints.Should().Be(1, "the second caller must fail fast inside the backoff, not re-POST");
    }

    [Fact]
    public async Task A_404_on_a_discovered_installation_id_is_forgetting_not_sticky()
    {
        // Uninstall/reinstall changes the installation id. The cached id must be dropped
        // on 404 so the next attempt (after the mint-failure backoff) re-discovers it,
        // rather than 404ing until the process restarts.
        var clock = new FakeClock(Now);
        var handler = new MintHandler("""{"token":"ghs_test","expires_at":"__EXPIRES_AT__"}""");
        var source = CreateMinting(handler, clock);

        // First mint: installation id 4242 is discovered and works.
        _ = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        _ = handler.InstallationLookups.Should().Be(1);

        // Expire the token, then 404 the next mint (the App was reinstalled).
        clock.Advance(Duration.FromMinutes(26));
        handler.MintStatusOverride = HttpStatusCode.NotFound;
        var act = () => source.GetTokenAsync(TestContext.Current.CancellationToken);
        _ = await act.Should().ThrowAsync<GitHubAuthException>();

        // Past the backoff, the next mint re-discovers the installation instead of
        // reusing the stale id.
        handler.MintStatusOverride = null;
        clock.Advance(Duration.FromMinutes(2));
        _ = await source.GetTokenAsync(TestContext.Current.CancellationToken);

        _ = handler.InstallationLookups.Should().Be(2, "a 404 on a discovered id must drop it so it is re-discovered");
    }
}
