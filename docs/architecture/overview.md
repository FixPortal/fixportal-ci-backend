# Architecture Overview — `fixportal-ci-backend`

> Derived from the knowledge graphs in `graphify-out/` (agent graph) and
> `.understand-anything/` (human graph). Both are regenerable build artifacts
> (`/graphify --update`, `/understand`) and are git-ignored. This document is the
> hand-curated reading of what those graphs surface — the *intent* and the
> *load-bearing seams*, not a file-by-file inventory.

## What this is

The backend for a read-only, **org-wide** CI/CD status board. Point it at a
GitHub org and it auto-discovers every non-archived repo and workflow, polls the
GitHub Actions API **server-side** with a read-only PAT, and exposes one snapshot
of build / deploy / package / PR / code-metrics signals over
`GET /api/dashboard/snapshot`. A single deployable ASP.NET Core **minimal API**
on .NET 10, **no database** — all state is an in-memory snapshot with a
last-known-good file backstop. The board UI is a separate concern: any client
can fetch this API cross-origin — the open-source `@fix-portal/ci-frontend`
board is one such UI.

## The spine (data flow)

```
GitHub Actions API  (read-only PAT)
        │  poll / clone
        ▼
  Integrations/GitHub/GitHubOrgClient ...... the single upstream gateway (god node, ~28 edges)
        │   repos · workflows · runs · jobs · merged PRs
        │   wrapped by the rate-limit-conservation layer:
        │     GitHubETagStore (conditional GET / If-None-Match → 304)
        │     GitHubInventoryCache (TTL repo/workflow inventory, single-flight)
        │     PerRepoCache (per-repo enrichment results)
        ▼
  Dashboard/HostedServices/*  ............... background workers, independent cadences
        RepoEnrichmentWorker (abstract base)
          ├─ DashboardRefreshWorker      60s  → DashboardRefreshService builds snapshot + 24h CI trend
          ├─ MetricsEnrichmentWorker     12h  → LizardScanner (NLOC / cyclomatic complexity)
          ├─ MergedPrEnrichmentWorker     5m  → org's most-recently-merged PR
          └─ JobLaneEnrichmentWorker      —   → named lanes (Deploys / Packages) from matching jobs
        │   write_to
        ▼
  Dashboard/Services/DashboardSnapshotState  in-memory last-known-good snapshot
        │   .ComputePublicSnapshot() ........ privacy projection — excludes private repos
        ▼
  Dashboard/Endpoints/DashboardEndpoints ... GET /api/dashboard/snapshot (the contract)
        │   non-API routes → 301 redirect to www.fixportal.org/ci
        ▼
  board UI (any snapshot consumer) ......... fetches this API cross-origin (CORS)
```

Persistence runs alongside the in-memory state:
`FileDashboardSnapshotStore` implements `IDashboardSnapshotStore`;
`SnapshotRestoreService` (an `IHostedLifecycleService`) **restores the last
snapshot on cold start** and saves on each refresh — so a restart serves stale
data immediately instead of blanking.

## Load-bearing seams (what the graph gets right)

- **`GitHubOrgClient` is the genuine god node** (~28 edges) — and unlike the
  false-gods in the frontends, this one is real: it is the *single* gateway to
  every GitHub Actions API call. Every worker depends on it. It is also the
  largest class; graphify flags low cohesion (0.08) and suggests splitting — a
  legitimate refactor target, not a measurement artifact.
- **The rate-limit-conservation layer is the reason this scales to a whole org.**
  ETag conditional GETs (`GitHubETagStore`) + TTL inventory cache
  (`GitHubInventoryCache`, single-flight so concurrent callers collapse into one
  fetch) + `PerRepoCache`. Without these, polling N repos every 60s would exhaust
  the GitHub rate limit. This is rationale-backed in `operator-handoff.md`.
- **`DashboardSnapshotState` + `ComputePublicSnapshot()` is the trust boundary.**
  The in-memory snapshot holds everything; the public projection strips private
  repos before the anonymous endpoint serves it. Get this projection wrong and
  private repo names leak. It is the security-critical method in the service.
- **The enrichment-worker pattern is the extension seam.** `RepoEnrichmentWorker`
  is the abstract base; each cadenced worker inherits it and writes its slice into
  the shared snapshot on its own timer. Adding a new signal = a new worker, no
  change to the endpoint or the refresh loop.
- **Last-known-good is a deliberate resilience choice** — a failed refresh keeps
  the prior snapshot rather than blanking the board. Rationale-backed.

## Structural findings worth recording

- **No import cycles** across the service.
- **`DashboardRefreshService` vs `DashboardRefreshWorker` split**: the *Worker* is
  the timer/lifecycle shell (`BackgroundService`); the *Service* holds the pure
  snapshot-and-trend building logic (`BuildCiTrend`, `BuildSummary`). The trend
  logic is the most heavily tested unit in the codebase — keep it pure.
- **Cross-repo contract:** the `DashboardSnapshot` shape this API emits is the
  same contract any client (e.g. `@fix-portal/ci-frontend`) consumes. The API and
  its UI are coupled only through this JSON shape and CORS — there is no shared
  package. Treat `DashboardModels.cs` as a published interface.
- **Lizard metrics shell out**: `LizardScanner` runs the external `lizard` tool
  via `ProcessRunner` against shallow clones — the one place the service spawns a
  subprocess and touches the filesystem (`MetricsWorkDirectory`). Slow cadence
  (12h) by design.
- The graphify (agent) graph includes the xUnit test project; this UE (human)
  graph excludes it via `.understandignore`. So graphify surfaces test-only bridge
  handlers (`CountingHandler`, `RecordingHandler`) as high-betweenness nodes —
  those are test scaffolding, not architecture.

## Layers (from the understand-anything graph)

1. **API Layer** — `Program.cs` (composition root: DI wiring, CORS, non-API redirect) + `DashboardEndpoints`
2. **Background Workers** — `RepoEnrichmentWorker` base + the 4 cadenced workers + `SnapshotRestoreService`
3. **Domain Services** — `DashboardRefreshService`, `DashboardSnapshotState`, `FileDashboardSnapshotStore`/`IDashboardSnapshotStore`, `GitHubInventoryCache`, `PerRepoCache`, `DashboardModels`
4. **External Integrations** — `GitHubOrgClient` + `GitHubETagStore` (GitHub); `LizardScanner` + `ProcessRunner` (Lizard)
5. **Configuration** — strongly-typed Options (`GitHubOptions`, `DashboardOptions`, `JobLaneOptions`, `AdminOptions`) + appsettings + csproj/sln/build props
6. **Infrastructure & CI/CD** — `Dockerfile`, `docker-compose.yml`, `deploy/bicep/main.bicep`, `ci.yml`, `mutation.yml`, `dependabot.yml`, bootstrap/summarize scripts
7. **Documentation** — `README.md`, `operator-handoff.md`, this architecture overview

## Deploy & operations

- **Container:** multi-stage `Dockerfile` (non-root, port 8080) — runs on any
  container platform; only the GitHub token + owner are required.
- **Azure:** CI builds the image once on Blacksmith, pushes it to GHCR, imports
  that exact commit-tagged image into ACR, then ships `deploy/bicep/main.bicep`
  to **Azure Container Apps** (single always-on
  replica) on every push to `main`. The Bicep template and workflow carry **no**
  subscription / registry / resource identifiers — those come from GitHub repo
  Secrets, so the template is reusable as-is. One-time OIDC bootstrap +
  secret/variable list live in `operator-handoff.md`.
- **CI quality gates:** `ci.yml` (format, build, test, CodeQL, GHCR publish, ACA
  deploy), `mutation.yml` (weekly/manual Stryker mutation testing), Dependabot.

## Where to start reading

`README.md` → `Program.cs` (composition root) → `GitHubOrgClient` (the upstream
gateway) → `DashboardRefreshService` (snapshot + CI trend build) →
`DashboardSnapshotState.ComputePublicSnapshot()` (the trust boundary) →
`DashboardEndpoints` (the contract). The `.understand-anything` dashboard tour
walks this same path in 15 steps.
