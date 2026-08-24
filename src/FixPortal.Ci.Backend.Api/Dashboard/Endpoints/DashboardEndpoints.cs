using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.Endpoints;

public sealed record MergePullRequestRequest(string Repo, int PullNumber);

public static class DashboardEndpoints
{
    // Fluent endpoint-map return values are part of the conventional extension API even when this host does not chain it.
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Served from the in-memory holder: no per-request disk I/O and no race
        // with the background writer. 204 until the first snapshot is available.
        // Private repositories are stripped before responding — the endpoint is
        // unauthenticated, so the server must enforce visibility, not the client.
        _ = endpoints.MapGet(
            "/api/dashboard/snapshot",
            (DashboardSnapshotState state, IOptions<AdminOptions> admin) =>
            {
                var snapshot = admin.Value.ExposePrivateToGuests ? state.Current : state.Public;
                if (snapshot is null)
                {
                    return Results.NoContent();
                }

                return Results.Ok(snapshot);
            }
        );

        // Full snapshot including private repositories. Protected by a shared
        // key that the host (simulator backend) holds server-side and forwards
        // as X-Admin-Key — the key never reaches the browser. Returns 401 when
        // the key is absent, wrong, or not configured (fail-closed).
        _ = endpoints.MapGet(
            "/api/dashboard/snapshot/admin",
            (HttpRequest request, HttpResponse response, DashboardSnapshotState state, IOptions<AdminOptions> admin) =>
            {
                PreventSensitiveCaching(response);
                if (!IsAdmin(request, admin.Value.AdminKey))
                {
                    return Results.Unauthorized();
                }

                var snapshot = state.Current;
                if (snapshot is null)
                {
                    return Results.NoContent();
                }

                return Results.Ok(snapshot);
            }
        );

        _ = endpoints.MapPost(
            "/api/dashboard/merge",
            async (
                HttpRequest request,
                HttpResponse response,
                DashboardSnapshotState state,
                GitHubOrgClient gitHub,
                IOptions<AdminOptions> admin,
                CancellationToken ct
            ) =>
            {
                PreventSensitiveCaching(response);
                if (!IsAdmin(request, admin.Value.AdminKey))
                {
                    return Error(HttpStatusCode.Unauthorized, "Unauthorized.");
                }

                MergePullRequestRequest? merge;
                try
                {
                    merge = await request.ReadFromJsonAsync<MergePullRequestRequest>(ct);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    return Error(HttpStatusCode.BadRequest, "Invalid request body.");
                }
                if (merge is null)
                {
                    return Error(HttpStatusCode.BadRequest, "Invalid request body.");
                }
                if (merge.PullNumber <= 0)
                {
                    return Error(HttpStatusCode.BadRequest, "Pull number must be greater than zero.");
                }

                var repository = state.Current?.Repositories.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, merge.Repo, StringComparison.OrdinalIgnoreCase)
                );
                if (repository is null)
                {
                    return Error(HttpStatusCode.NotFound, "Repository not found.");
                }

                GitHubMergeResult result;
                try
                {
                    result = await gitHub.MergePullRequestAsync(repository.Name, merge.PullNumber, ct);
                }
                catch (Exception ex)
                    when (ex is GitHubAuthException or GitHubRateLimitException or HttpRequestException or JsonException
                        || ex is OperationCanceledException && !ct.IsCancellationRequested
                    )
                {
                    return Error(HttpStatusCode.BadGateway, "GitHub merge request failed.");
                }

                if (result.StatusCode == HttpStatusCode.OK && result.Merged && !string.IsNullOrWhiteSpace(result.Sha))
                {
                    return Results.Ok(new { merged = true, sha = result.Sha });
                }

                var error = string.IsNullOrWhiteSpace(result.Message) ? "GitHub merge request failed." : result.Message;
                return result.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict
                    ? Error(HttpStatusCode.Conflict, error)
                    : Error(HttpStatusCode.BadGateway, error);
            }
        );

        // Health-check endpoint surfacing GitHub credential status (M8). Unauthenticated,
        // so it must NOT echo the raw auth-error string: that string embeds the failing
        // request URL including the private repo name (repos/{owner}/{repo}/...), which
        // would leak private repo names to anonymous callers. Return a generic message;
        // the detailed error is already logged server-side (DashboardRefreshService).
        _ = endpoints.MapGet(
            "/api/health",
            (DashboardSnapshotState state) =>
            {
                if (state.LastAuthError is not null)
                {
                    return Results.Json(
                        new { Status = "Degraded", Error = "GitHub credential check failing" },
                        statusCode: 503
                    );
                }

                return Results.Ok(new { Status = "Healthy" });
            }
        );

        return endpoints;
    }

    private static bool IsAdmin(HttpRequest request, string? configured)
    {
        var provided = request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
        return !string.IsNullOrEmpty(configured)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(configured)
            );
    }

    private static void PreventSensitiveCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-store";
        response.Headers.Vary = "X-Admin-Key";
    }

    private static IResult Error(HttpStatusCode status, string message) =>
        Results.Json(new { error = message }, statusCode: (int)status);
}
