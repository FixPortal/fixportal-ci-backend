using System.Text.Json;
using System.Text.Json.Serialization;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Endpoints;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Integrations.GitHub;
using FixPortal.Ci.Backend.Api.Integrations.Lizard;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    _ = options.SerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton(TimeProvider.System);

// Fail fast on misconfiguration rather than polling forever with silent 401s / empty dashboards.
builder
    .Services.AddOptions<GitHubOptions>()
    .Bind(builder.Configuration.GetSection("GitHub"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Owner), "GitHub:Owner must be configured (e.g. set GitHub__Owner).")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Token), "GitHub:Token must be configured (e.g. set GitHub__Token).")
    .ValidateOnStart();
builder
    .Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection("Dashboard"))
    .Validate(o => o.RefreshSeconds > 0, "Dashboard:RefreshSeconds must be greater than zero.")
    .Validate(o => o.MetricsRefreshSeconds > 0, "Dashboard:MetricsRefreshSeconds must be greater than zero.")
    .Validate(o => o.MergedPrRefreshSeconds > 0, "Dashboard:MergedPrRefreshSeconds must be greater than zero.")
    .Validate(
        o => o.GetEffectiveJobLanes().All(l => l.RefreshSeconds > 0),
        "Dashboard:JobLanes:RefreshSeconds must be greater than zero."
    )
    .Validate(
        o => o.GetEffectiveJobLanes().All(l => l.MaxRunsToScan > 0),
        "Dashboard:JobLanes:MaxRunsToScan must be greater than zero."
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection("Admin"))
    // An empty AdminKey is valid and fails closed: the admin endpoint returns 401
    // unconditionally when no key is configured. But a *set* key that is implausibly
    // short is almost certainly a truncated/typo'd secret — reject it at startup
    // rather than shipping a guessable admin key.
    .Validate(o => o.HasValidAdminKeyLength(), "Admin:AdminKey, when set, must be at least 16 characters.")
    .ValidateOnStart();

// CORS so the FixPortal SPA (a separate origin) can read the public snapshot.
// Empty config -> no origins allowed (safe default until the SPA origin is set
// via Cors__AllowedOrigins__0 in deploy config).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("FixPortalSpa", policy => policy.WithOrigins(corsOrigins).WithMethods("GET").AllowAnyHeader());
});

// Singleton so the per-URL ETag cache outlives the transient typed-client instances
// and persists across refresh cycles — conditional GETs (304s) are what keep the 20s
// poll cadence within the GitHub PAT rate budget.
builder.Services.AddSingleton<GitHubETagStore>();
builder
    .Services.AddHttpClient<GitHubOrgClient>(client =>
    {
        // GitHub's REST API root is a fixed, well-known absolute URI, not a
        // deployment-specific path, so S1075 (no hardcoded URIs) does not apply.
#pragma warning disable S1075
        client.BaseAddress = new Uri("https://api.github.com/");
#pragma warning restore S1075
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            // GitHubOrgClient is a typed client but it is captured by singletons
            // (DashboardRefreshService, GitHubInventoryCache, the job-lane workers), so
            // its HttpClient lives for the whole process and the factory's handler
            // rotation never fires. PooledConnectionLifetime recycles pooled connections
            // at the socket level regardless, so DNS changes for api.github.com are
            // picked up on a long-running container instead of being pinned forever.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        }
    );
builder.Services.AddSingleton<DashboardSnapshotState>();
builder.Services.AddSingleton<IDashboardSnapshotStore>(sp =>
{
    var dashboardOptions = sp.GetRequiredService<IOptions<DashboardOptions>>().Value;
    var environment = sp.GetRequiredService<IHostEnvironment>();

    // Resolve a relative SnapshotPath against the content root so it does not
    // land in the process working directory (which is not the app root under
    // IIS / App Service).
    var path = Path.IsPathRooted(dashboardOptions.SnapshotPath)
        ? dashboardOptions.SnapshotPath
        : Path.Combine(environment.ContentRootPath, dashboardOptions.SnapshotPath);
    return new FileDashboardSnapshotStore(path);
});

builder.Services.AddSingleton<GitHubInventoryCache>();
builder.Services.AddSingleton<PerRepoCache<RepoMetrics>>();
builder.Services.AddSingleton<PerRepoCache<MergedPullRequest>>();

// Last-known-good, no TTL — consistent with the metrics and merged-PR caches above.
// A max-age here was ineffective: InheritEnrichment re-inherits an expired lane
// signal from the previous snapshot every cycle (needed for cold-start continuity),
// so the TTL only ever bit before the first snapshot existed. Dropping it makes the
// real behaviour explicit — a persistently-failing lane keeps its last chip, exactly
// as a stale metric or merged-PR does — rather than documenting a stale-out the code
// never delivered.
builder.Services.AddKeyedSingleton<PerRepoCache<IReadOnlyList<JobSignal>>>(
    "deploys",
    (_, _) => new PerRepoCache<IReadOnlyList<JobSignal>>()
);
builder.Services.AddKeyedSingleton<PerRepoCache<IReadOnlyList<JobSignal>>>(
    "packages",
    (_, _) => new PerRepoCache<IReadOnlyList<JobSignal>>()
);
builder.Services.AddSingleton<LizardScanner>();
builder.Services.AddSingleton<DashboardRefreshService>();
builder.Services.AddHostedService<SnapshotRestoreService>();
builder.Services.AddHostedService<DashboardRefreshWorker>();
builder.Services.AddHostedService<MetricsEnrichmentWorker>();
builder.Services.AddHostedService<MergedPrEnrichmentWorker>();
builder.Services.AddSingleton<IHostedService>(sp => new JobLaneEnrichmentWorker(
    "deploys",
    sp.GetRequiredService<GitHubOrgClient>(),
    sp.GetRequiredService<GitHubInventoryCache>(),
    sp.GetRequiredKeyedService<PerRepoCache<IReadOnlyList<JobSignal>>>("deploys"),
    sp.GetRequiredService<IOptions<DashboardOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<JobLaneEnrichmentWorker>>()
));
builder.Services.AddSingleton<IHostedService>(sp => new JobLaneEnrichmentWorker(
    "packages",
    sp.GetRequiredService<GitHubOrgClient>(),
    sp.GetRequiredService<GitHubInventoryCache>(),
    sp.GetRequiredKeyedService<PerRepoCache<IReadOnlyList<JobSignal>>>("packages"),
    sp.GetRequiredService<IOptions<DashboardOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<JobLaneEnrichmentWorker>>()
));

var app = builder.Build();

var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
var registeredKeys = new HashSet<string>(["deploys", "packages"], StringComparer.OrdinalIgnoreCase);

// Log the lanes actually in force (config binding is append-not-replace prone) so a
// silently-ignored or mis-cadenced lane is visible at startup rather than a mystery.
var effectiveLanes = dashboardOptions.GetEffectiveJobLanes();
foreach (var lane in effectiveLanes)
{
    app.Logger.LogInformation(
        "JobLane '{Key}' effective: enabled={Enabled}, refresh={Refresh}s, maxRuns={MaxRuns}.",
        lane.Key,
        lane.Enabled,
        lane.RefreshSeconds,
        lane.MaxRunsToScan
    );
}
foreach (var lane in effectiveLanes.Where(l => !registeredKeys.Contains(l.Key)))
{
    app.Logger.LogWarning(
        "Configured JobLane '{Key}' is unregistered; no worker or cache exists for it and it will be ignored.",
        lane.Key
    );
}

if (corsOrigins.Length == 0)
{
    app.Logger.LogWarning(
        "Cors:AllowedOrigins is not configured; all cross-origin requests will be blocked. Set Cors__AllowedOrigins__0 in deploy config to allow the frontend SPA."
    );
}

// API docs are a development aid, not a public production surface.
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
    _ = app.MapScalarApiReference();
}

app.UseCors("FixPortalSpa");

app.MapDashboardEndpoints();

// Unmatched API routes return 404. Non-API routes redirect permanently to the
// canonical UI location — the CI board is now served by fixportal-simulator-frontend.
// The destination is a fixed, well-known absolute URI, so S1075 (no hardcoded URIs)
// does not apply.
#pragma warning disable S1075
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallback(() => Results.Redirect("https://www.fixportal.org/ci", permanent: true));
#pragma warning restore S1075

await app.RunAsync();

// Exposed as a public partial class so WebApplicationFactory<Program> can
// reference the entry point from the test project — not a utility class, so
// S1118 (utility classes should not have a public constructor) does not apply.
#pragma warning disable S1118
// Referenced by WebApplicationFactory from the test assembly.
// ReSharper disable once ClassNeverInstantiated.Global
public partial class Program;
#pragma warning restore S1118
