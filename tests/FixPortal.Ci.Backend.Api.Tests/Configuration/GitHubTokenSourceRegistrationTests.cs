using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Ide;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Configuration;

public class GitHubTokenSourceRegistrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private WebApplicationFactory<Program> ConfigureFactory(bool appCredentialsConfigured) =>
        factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            _ = builder.UseSetting("GitHub:Token", "test-pat");
            if (appCredentialsConfigured)
            {
                _ = builder.UseSetting("GitHubApp:AppId", "123456");
                _ = builder.UseSetting("GitHubApp:PrivateKeyPem", "-----BEGIN PRIVATE KEY-----test");
            }
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });

    [Fact]
    public async Task Missing_app_credentials_select_the_configured_PAT_source()
    {
        using var app = ConfigureFactory(appCredentialsConfigured: false);
        var source = app.Services.GetRequiredService<IGitHubTokenSource>();

        _ = source.Should().BeOfType<StaticGitHubTokenSource>();
        var token = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        _ = token.Should().Be("test-pat");
    }

    [Fact]
    public void A_configured_private_key_without_a_PEM_marker_fails_startup()
    {
        using var app = factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("GitHub:Owner", "FixPortal");
            _ = builder.UseSetting("GitHub:Token", "test-pat");
            _ = builder.UseSetting("GitHubApp:AppId", "123456");
            // Complete credentials, but the value is not PEM-shaped: no "PRIVATE KEY" marker.
            _ = builder.UseSetting("GitHubApp:PrivateKeyPem", "not-a-pem-key");
            _ = builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });

        // Trigger host startup itself (ValidateOnStart), not IOptions<T>.Value access: the
        // latter validates eagerly regardless of ValidateOnStart, so asserting on it alone
        // would keep passing even if ValidateOnStart were removed from the registration.
        var act = () => _ = app.Services;

        _ = act.Should()
            .Throw<OptionsValidationException>()
            .WithMessage("*PrivateKeyPem does not look like a PEM key*");
    }

    [Fact]
    public void Complete_app_credentials_select_the_installation_token_source()
    {
        using var app = ConfigureFactory(appCredentialsConfigured: true);

        _ = app.Services.GetRequiredService<IGitHubTokenSource>().Should().BeOfType<GitHubAppTokenSource>();
    }

    [Fact]
    public void Diagnosis_http_client_registration_disables_transport_redirects()
    {
        using var app = ConfigureFactory(appCredentialsConfigured: false);
        using var client = app.CreateClient();
        var handlers = app.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = handlers.CreateHandler(typeof(RunDiagnosisReader).Name);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        _ = handler.Should().BeOfType<SocketsHttpHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }
}
