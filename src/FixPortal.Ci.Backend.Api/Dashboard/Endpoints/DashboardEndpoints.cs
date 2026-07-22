using System.Security.Cryptography;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Dashboard.Endpoints;

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
            (HttpRequest request, DashboardSnapshotState state, IOptions<AdminOptions> admin) =>
            {
                var configured = admin.Value.AdminKey;
                var provided = request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
                if (
                    string.IsNullOrEmpty(configured)
                    || !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(provided),
                        System.Text.Encoding.UTF8.GetBytes(configured)
                    )
                )
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
}
