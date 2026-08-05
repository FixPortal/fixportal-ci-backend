using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Microsoft.Extensions.Options;
using NodaTime.Serialization.SystemTextJson;

namespace FixPortal.Ci.Backend.Api.Ide;

public static class IdeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static IEndpointRouteBuilder MapIdeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapGet(
            "/api/ide/v1/snapshot",
            (HttpRequest request, HttpResponse response, DashboardSnapshotState state, IOptions<IdeIntegrationOptions> ide, IOptions<DashboardOptions> dashboard) =>
            {
                var configured = ide.Value.ApiKey;
                var provided = request.Headers["X-CI-IDE-Key"].FirstOrDefault() ?? "";
                if (
                    string.IsNullOrEmpty(configured)
                    || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured))
                )
                {
                    return Results.Unauthorized();
                }

                var current = state.Current;
                if (current is null)
                {
                    return Results.NoContent();
                }

                var snapshot = Project(current, dashboard.Value.RunHistoryPageSize);
                var projection = new
                {
                    snapshot.SchemaVersion,
                    SnapshotId = "",
                    ObservedAt = "",
                    snapshot.Organization,
                    snapshot.Repositories,
                };
                var snapshotId = $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection, JsonOptions))).ToLowerInvariant()}";
                var completed = snapshot with { SnapshotId = snapshotId };
                var etag = $"W/\"{snapshotId}\"";

                response.Headers.ETag = etag;
                if (request.Headers["If-None-Match"].Any(value => value is not null && value.Split(',').Any(tag => string.Equals(tag.Trim(), etag, StringComparison.Ordinal))))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(completed, JsonOptions), "application/json");
            }
        );

        return endpoints;
    }

    private static IdeSnapshot Project(DashboardSnapshot snapshot, int runHistoryPageSize) =>
        new(
            1,
            "",
            snapshot.RefreshedAt,
            snapshot.Org,
            snapshot.Repositories
                .OrderBy(repository => repository.Name, StringComparer.Ordinal)
                .Select(repository =>
                    new IdeRepository(
                        repository.Name,
                        repository.Private,
                        repository.Workflows
                            .OrderBy(workflow => workflow.File, StringComparer.Ordinal)
                            .Select(workflow =>
                                new IdeWorkflow(
                                    workflow.File,
                                    (workflow.RecentRuns ?? [])
                                        .Where(run => IsEligible(run, workflow.File))
                                        .OrderByDescending(run => run.UpdatedAt)
                                        .ThenByDescending(run => run.ProviderRunId)
                                        .ThenByDescending(run => run.RunAttempt)
                                        .Take(runHistoryPageSize)
                                        .Select(run =>
                                            new IdeRun(
                                                run.ProviderRunId!.Value,
                                                run.RunAttempt!.Value,
                                                run.HeadSha!,
                                                run.Branch,
                                                run.Event,
                                                run.Status,
                                                run.Conclusion,
                                                run.HtmlUrl,
                                                run.UpdatedAt
                                            )
                                        )
                                        .ToList()
                                )
                            )
                            .ToList()
                    )
                )
                .ToList()
        );

    private static bool IsEligible(WorkflowRun run, string workflowFile) =>
        run.ProviderRunId > 0
        && run.RunAttempt > 0
        && !string.IsNullOrWhiteSpace(run.WorkflowFile)
        && string.Equals(run.WorkflowFile, workflowFile, StringComparison.Ordinal)
        && run.HeadSha is { Length: 40 } sha
        && sha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = options.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);
        return options;
    }
}
