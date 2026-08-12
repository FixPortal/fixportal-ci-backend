using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using Microsoft.Extensions.Options;
using NodaTime.Serialization.SystemTextJson;

namespace FixPortal.Ci.Backend.Api.Ide;

public static class IdeEndpoints
{
    private const string WorkflowDirectory = ".github/workflows/";
    private const int MaximumWorkflowFileBytes = 120;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    // void rather than a fluent return: Program.cs calls this once and never chains, so a
    // returned builder is a value nothing reads.
    public static void MapIdeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.MapGet(
            "/api/ide/v1/snapshot",
            (
                HttpRequest request,
                HttpResponse response,
                DashboardSnapshotState state,
                IOptions<IdeIntegrationOptions> ide,
                IOptions<DashboardOptions> dashboard
            ) =>
            {
                PreventSensitiveCaching(response);
                if (!IsAuthorized(request, ide.Value.ApiKey))
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
                var snapshotId =
                    $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection, JsonOptions))).ToLowerInvariant()}";
                var completed = snapshot with { SnapshotId = snapshotId };
                var etag = $"W/\"{snapshotId}\"";

                response.Headers.ETag = etag;
                if (
                    request
                        .Headers["If-None-Match"]
                        .Any(value =>
                            value is not null
                            && value.Split(',').Any(tag => string.Equals(tag.Trim(), etag, StringComparison.Ordinal))
                        )
                )
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(completed, JsonOptions), "application/json");
            }
        );

        _ = endpoints.MapGet("/api/ide/v1/repositories/{repository}/runs/{runId:long}/diagnosis", HandleDiagnosisAsync);
    }

    private static async Task<IResult> HandleDiagnosisAsync(HttpContext context, string repository, long runId)
    {
        PreventSensitiveCaching(context.Response);
        var services = context.RequestServices;
        var ide = services.GetRequiredService<IOptions<IdeIntegrationOptions>>();
        if (!IsAuthorized(context.Request, ide.Value.ApiKey))
        {
            return Results.Unauthorized();
        }

        var state = services.GetRequiredService<DashboardSnapshotState>();
        var gitHub = services.GetRequiredService<IOptions<GitHubOptions>>().Value;
        var match = FindRun(state.Current, gitHub.Owner, repository, runId);
        if (match is null)
        {
            return DiagnosisError(StatusCodes.Status404NotFound, "Diagnosis is unavailable.");
        }

        var result = await services
            .GetRequiredService<RunDiagnosisReader>()
            .ReadAsync(match.Value.Repository.Name, runId, context.RequestAborted);
        return result.Status switch
        {
            RunDiagnosisReadStatus.Available => DiagnosisJson(
                new IdeRunDiagnosis(
                    1,
                    $"{gitHub.Owner}/{match.Value.Repository.Name}",
                    runId,
                    match.Value.Run.RunAttempt!.Value,
                    match.Value.Run.HeadSha!,
                    result.Content!.TextSha256,
                    result.Content.Truncated,
                    result.Content.Excerpt
                )
            ),
            RunDiagnosisReadStatus.Unavailable => DiagnosisError(
                StatusCodes.Status404NotFound,
                "Diagnosis is unavailable."
            ),
            RunDiagnosisReadStatus.TimedOut => DiagnosisError(
                StatusCodes.Status504GatewayTimeout,
                "Diagnosis provider timed out."
            ),
            _ => DiagnosisError(StatusCodes.Status502BadGateway, "Diagnosis provider response was rejected."),
        };
    }

    private static (RepositorySnapshot Repository, WorkflowRun Run)? FindRun(
        DashboardSnapshot? snapshot,
        string owner,
        string repository,
        long runId
    )
    {
        if (snapshot is null || !string.Equals(snapshot.Org, owner, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var repositories = snapshot
            .Repositories.Where(candidate =>
                string.Equals(candidate.Name, repository, StringComparison.OrdinalIgnoreCase)
            )
            .Take(2)
            .ToList();
        if (repositories.Count != 1 || runId <= 0)
        {
            return null;
        }
        var found = repositories[0];

        var matches = CanonicalWorkflows(found.Workflows)
            .SelectMany(workflow =>
                (workflow.Workflow.RecentRuns ?? [])
                    .Where(run =>
                        run.ProviderRunId == runId
                        && run is { RunAttempt: > 0, Status: "completed", Conclusion: "failure" }
                        && string.Equals(run.Repository, $"{owner}/{found.Name}", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            CanonicalWorkflowFile(run.WorkflowFile),
                            workflow.File,
                            StringComparison.Ordinal
                        )
                        && IsCanonicalSha(run.HeadSha)
                    )
                    .Select(run => (found, run))
            )
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool IsAuthorized(HttpRequest request, string? configured)
    {
        var provided = request.Headers["X-CI-IDE-Key"].FirstOrDefault() ?? "";
        return !string.IsNullOrEmpty(configured)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(configured)
            );
    }

    private static void PreventSensitiveCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Vary = "X-CI-IDE-Key";
    }

    private static bool IsCanonicalSha(string? sha) =>
        sha is { Length: 40 } && sha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IResult DiagnosisError(int statusCode, string message) =>
        Results.Json(new IdeDiagnosisError(message), statusCode: statusCode);

    private static IResult DiagnosisJson(IdeRunDiagnosis diagnosis)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(diagnosis, JsonOptions);
        return Results.Bytes([.. json, (byte)'\n'], "application/json");
    }

    private static IdeSnapshot Project(DashboardSnapshot snapshot, int runHistoryPageSize) =>
        new(
            1,
            "",
            snapshot.RefreshedAt,
            snapshot.Org,
            snapshot
                .Repositories.OrderBy(repository => repository.Name, StringComparer.Ordinal)
                .Select(repository => new IdeRepository(
                    repository.Name,
                    repository.Private,
                    CanonicalWorkflows(repository.Workflows)
                        .OrderBy(workflow => workflow.File, StringComparer.Ordinal)
                        .Select(workflow => new IdeWorkflow(
                            workflow.File,
                            (workflow.Workflow.RecentRuns ?? [])
                                .Where(run => IsEligible(run, workflow.File))
                                .OrderByDescending(run => run.UpdatedAt)
                                .ThenByDescending(run => run.ProviderRunId)
                                .ThenByDescending(run => run.RunAttempt)
                                .Take(runHistoryPageSize)
                                .Select(run => new IdeRun(
                                    run.ProviderRunId!.Value,
                                    run.RunAttempt!.Value,
                                    run.HeadSha!,
                                    run.Branch,
                                    run.Event,
                                    run.Status,
                                    run.Conclusion,
                                    run.HtmlUrl,
                                    run.UpdatedAt
                                ))
                                .ToList()
                        ))
                        .ToList()
                ))
                .ToList()
        );

    private static bool IsEligible(WorkflowRun run, string workflowFile) =>
        run is { ProviderRunId: > 0, RunAttempt: > 0 }
        && string.Equals(CanonicalWorkflowFile(run.WorkflowFile), workflowFile, StringComparison.Ordinal)
        && run.HeadSha is { Length: 40 } sha
        && sha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IEnumerable<(WorkflowSnapshot Workflow, string File)> CanonicalWorkflows(
        IEnumerable<WorkflowSnapshot> workflows
    ) =>
        workflows
            .Select(workflow => (Workflow: workflow, File: CanonicalWorkflowFile(workflow.File)))
            .Where(workflow => workflow.File is not null)
            .GroupBy(workflow => workflow.File!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => (group.Single().Workflow, group.Key));

    private static string? CanonicalWorkflowFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var file = value.StartsWith(WorkflowDirectory, StringComparison.Ordinal)
            ? value[WorkflowDirectory.Length..]
            : value;
        if (
            string.IsNullOrWhiteSpace(file)
            || file == "."
            || file.Contains('/')
            || file.Contains('\\')
            || file.Contains("..", StringComparison.Ordinal)
            || file.Any(char.IsControl)
        )
        {
            return null;
        }

        var canonical = $"{WorkflowDirectory}{file}";
        return Encoding.UTF8.GetByteCount(canonical) <= MaximumWorkflowFileBytes ? canonical : null;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = options.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);
        return options;
    }
}
