using System.Net;
using System.Text;
using AwesomeAssertions;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FixPortal.Ci.Backend.Api.Tests.Integrations;

public class GitHubAuthorizationTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    [Fact]
    public async Task ListRepositories_keeps_pat_out_of_url_and_sends_it_as_bearer_authorization()
    {
        const string token = "pat-sentinel-7f2c";
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubOrgClient(
            http,
            Options.Create(new GitHubOptions { Owner = "FixPortal", Token = token }),
            Options.Create(new DashboardOptions { SnapshotPath = "snapshot.json", RefreshSeconds = 60 }),
            new GitHubETagStore()
        );

        _ = await client.ListRepositoriesAsync(CancellationToken.None);

        _ = handler.RequestUri.Should().NotBeNull();
        _ = handler.RequestUri!.ToString().Should().NotContain(token);
        _ = handler.AuthorizationScheme.Should().Be("Bearer");
        _ = handler.AuthorizationParameter.Should().Be(token);
    }
}
