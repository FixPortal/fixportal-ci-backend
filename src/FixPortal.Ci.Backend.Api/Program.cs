using System.Text.Json;
using System.Text.Json.Serialization;
using FixPortal.Ci.Backend.Api.Dashboard.Configuration;
using FixPortal.Ci.Backend.Api.Dashboard.Endpoints;
using FixPortal.Ci.Backend.Api.Dashboard.HostedServices;
using FixPortal.Ci.Backend.Api.Dashboard.Model;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using FixPortal.Ci.Backend.Api.Ide;
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

// GitHub App credentials are optional: unset, the dashboard keeps authenticating with the
// personal access token exactly as before. Set, API calls authenticate as an installation
// instead — which is the only way to read check runs (a fine-grained PAT is refused on
// statusCheckRollup, and there is no "Checks" permission to grant it), and which moves the
// dashboard off the GraphQL points budget it otherwise shares with whoever is running gh.
builder
    .Services.AddOptions<GitHubAppOptions>()
    .Bind(builder.Configuration.GetSection("GitHubApp"))
    .Validate(
        o => !o.IsConfigured || o.PrivateKeyPem!.Contains("PRIVATE KEY", StringComparison.Ordinal),
        "GitHubApp:PrivateKeyPem does not look like a PEM key. Paste the .pem file's contents, not its path."
    )
    .ValidateOnStart();

// A named client, not the typed GitHubOrgClient one: minting a token must not recurse
// through the client whose requests are waiting on that token.
builder.Services.AddHttpClient<GitHubAppTokenSource>(client =>
{
    // Same fixed, well-known API root as the typed client below; S1075 does not apply.
#pragma warning disable S1075
    client.BaseAddress = new Uri("https://api.github.com/");
#pragma warning restore S1075
});

builder.Services.AddSingleton<IGitHubTokenSource>(sp =>
    sp.GetRequiredService<IOptions<GitHubAppOptions>>().Value.IsConfigured
        ? sp.GetRequiredService<GitHubAppTokenSource>()
        : new StaticGitHubTokenSource(sp.GetRequiredService<IOptions<GitHubOptions>>())
);

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
builder
    .Services.AddOptions<IdeIntegrationOptions>()
    .Bind(builder.Configuration.GetSection("IdeIntegration"))
    .Validate(
        options => options.IsValid(builder.Configuration["Admin:AdminKey"]),
        "IdeIntegration:ApiKey, when set, must be unpadded, resolved, at least 32 characters, and distinct from Admin:AdminKey."
    )
    .ValidateOnStart();

// RefreshSeconds reaches PeriodicTimer on the enrichment worker, and zero throws
// there — after cold start, on a background service whose default failure
// behaviour stops the host. The registration also validates reviewer identity
// because the configuration binder does not honour `required`.
builder.Services.AddReviewSignalsOptions(builder.Configuration);

// Merge state has no roster to validate — it is on by default with only a cadence to
// check — so it binds directly rather than through an extension like the one above.
MergeStateOptions.AddMergeStateOptions(builder.Services, builder.Configuration);

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
builder
    .Services.AddHttpClient<RunDiagnosisReader>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
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

// Unlike its neighbours above, this cache IS given a max-age. An expired review
// signal must read as "unknown" (no pills), never as a stale pass: while
// CollectAsync keeps soft-failing (GraphQL 5xx, rate limit, a PAT losing a scope
// — all caught and returned as null), the base RepoEnrichmentWorker preserves
// last-known-good forever, and the board refresh keeps re-attaching those stale
// signals to a PR that has since been pushed to. That is the exact false-pass the
// head-scoping in ReviewSignalFactory/PrReviewFacts exists to prevent, so it must
// not be reintroduced at the cache layer. TTL is 3x the configured refresh interval
// so one or two transient soft-fails do not expire a signal that is still current.
//
// TWO separate mechanisms could otherwise re-inherit a stale signal past this TTL,
// and both are closed. InheritEnrichment never carries PullRequests — only
// Metrics/Deploys/Packages/LastMergedPr — so it cannot. MergeWithPrevious CAN: it
// substitutes a failed repo's whole prior RepositorySnapshot, PullRequests included,
// and chains it forward every cycle, which would outlive this TTL entirely; it
// therefore strips head-scoped review state from anything it reinstates (see
// DashboardRefreshService.WithoutHeadScopedReviewState). With both closed, an expired entry
// genuinely produces no review-derived state rather than persisting through the
// "ineffective TTL" path the comment below documents for the job-lane caches.
builder.Services.AddSingleton(sp =>
{
    var reviewSignals = sp.GetRequiredService<IOptions<ReviewSignalsOptions>>().Value;
    return new PerRepoCache<IReadOnlyDictionary<int, CachedReviewSignals>>(
        sp.GetRequiredService<IClock>(),
        Duration.FromSeconds(3 * reviewSignals.RefreshSeconds)
    );
});

// Given a max-age for the same reason as the review-signal cache above: a stale merge
// verdict is worse than none. It is the one input to the board's ready-to-merge filter
// that another pull request's merge can invalidate remotely, so an entry that has stopped
// being refreshed must decay to "unknown" rather than keep asserting CLEAN. Three sweeps'
// grace, matching the review-signal cache's ratio.
builder.Services.AddSingleton(sp =>
{
    var mergeState = sp.GetRequiredService<IOptions<MergeStateOptions>>().Value;
    return new PerRepoCache<IReadOnlyDictionary<int, PrMergeState>>(
        sp.GetRequiredService<IClock>(),
        Duration.FromSeconds(3 * mergeState.RefreshSeconds)
    );
});

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
builder.Services.AddHostedService<ReviewSignalEnrichmentWorker>();
builder.Services.AddHostedService<MergeStateEnrichmentWorker>();
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

if (string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<IdeIntegrationOptions>>().Value.ApiKey))
{
    app.Logger.LogWarning("IdeIntegration:ApiKey is not configured; /api/ide/v1/* will return 401.");
}

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
app.MapIdeEndpoints();

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
