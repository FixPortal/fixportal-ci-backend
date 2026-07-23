---
title: FixPortal CI Dashboard — Backend API
date: 2026-05-31
status: active
repo: FixPortal/fixportal-ci-backend
stack: .NET 10 · ASP.NET Core minimal API · NodaTime
license: Apache-2.0
---

![Build](https://github.com/FixPortal/fixportal-ci-backend/actions/workflows/ci.yml/badge.svg)
![License](https://img.shields.io/github/license/FixPortal/fixportal-ci-backend)

# FixPortal CI Dashboard — Backend API

> The backend for a read-only, **org-wide** CI/CD status board. Point it at a
> GitHub org and it auto-discovers every repository and workflow, polls the
> GitHub Actions API server-side, and exposes a single snapshot of build, deploy,
> package, PR, and code-metrics signals over `GET /api/dashboard/snapshot`. Any
> client can render that snapshot; the open-source
> [`@fix-portal/ci-frontend`](https://github.com/FixPortal/fixportal-ci-frontend)
> board is one such UI. This service is a pure ASP.NET Core API with no
> database — all calls are server-side with a read-only PAT.

## What it does

Give it a `GitHub:Owner` and a read-only token, and it surfaces — for **every**
non-archived repo in that org, with no per-repo configuration:

- **Workflow status** — latest run state per workflow (success / failure /
  running / no-CI), with deep links out to the run.
- **Job lanes** — named lanes (e.g. *Deploys*, *Packages*) that break out
  matching workflow **jobs** as their own status chips, configurable by name
  pattern.
- **Open PRs** and the org's **most-recently-merged PR**.
- **Code metrics** — NLOC and cyclomatic complexity per repo via
  [Lizard](https://github.com/terryyin/lizard), refreshed on a slow cadence.
- **A "No CI" treatment** for repos with no workflows, plus a hide toggle.

It is deliberately **read-only**: the token grants only *Read* permissions, the
token never reaches the browser, and the board keeps the last-known-good snapshot
if a refresh fails rather than blanking.

## Architecture

A single deployable ASP.NET Core API. Background hosted services enrich an
in-memory snapshot on independent cadences; the HTTP endpoint just hands out the
latest snapshot. Non-API routes 301-redirect to the canonical board at
`www.fixportal.org/ci`, where the board UI (any snapshot consumer, e.g. the
open-source `@fix-portal/ci-frontend`) fetches this API cross-origin.

```mermaid
graph LR
    subgraph App["ASP.NET Core API"]
        direction TB
        RW["DashboardRefreshWorker (60s)<br/>workflows + states"]
        MW["MetricsEnrichmentWorker (12h)<br/>Lizard NLOC / CCN"]
        PW["MergedPrEnrichmentWorker (5m)"]
        JW["JobLaneEnrichmentWorker ×N<br/>deploys / packages"]
        SNAP[("In-memory snapshot<br/>+ last-known-good file")]
        API["/api/dashboard/snapshot/"]
        RW --> SNAP
        MW --> SNAP
        PW --> SNAP
        JW --> SNAP
        SNAP --> API
    end
    GH["GitHub Actions API<br/>(read-only PAT)"]
    SPA["Board UI<br/>(any snapshot consumer,<br/>e.g. @fix-portal/ci-frontend)"]
    RW -.poll.-> GH
    MW -.clone.-> GH
    PW -.poll.-> GH
    JW -.poll.-> GH
    API -.CORS.-> SPA
```

| Layer | Tech |
|---|---|
| API | .NET 10, ASP.NET Core **minimal API**, NodaTime, OpenAPI + Scalar (dev only) |
| Metrics | Lizard (`1.22.2`), run against shallow clones |
| Host | Azure Container Apps (single always-on replica) |
| CI | GitHub Actions — .NET build/test, scheduled Stryker mutation testing, CodeQL, Dependabot |

## Compatibility

| Runtime | Support |
|---|---|
| .NET 10 | Required — no down-level targets |
| Docker (any OCI host) | Supported via multi-stage `Dockerfile` (non-root, port 8080) |
| Azure Container Apps | Primary deployment target |

## Quick start — Docker Compose (full stack)

The easiest way to run a complete dashboard instance. Requires **Docker** and a
GitHub fine-grained PAT (see [operator-handoff.md](operator-handoff.md#github)
for exact scopes).

```
cp .env.example .env
# Edit .env: set GITHUB_TOKEN and GITHUB_OWNER
docker compose up
```

Open `http://localhost` for the board. The backend snapshot API is at
`http://localhost:5049/api/dashboard/snapshot`.

The `frontend` service uses the board UI image published from
[fixportal-ci-frontend](https://github.com/FixPortal/fixportal-ci-frontend).
Both images are on GHCR and updated on every push to `main`.

## Quick start (API only / development)

Prerequisites: **.NET 10 SDK** and a fine-grained GitHub PAT (see
[operator-handoff.md](operator-handoff.md#github) for exact scopes — read-only
**Actions** at minimum; add **Pull requests** and **Contents** for PR and metrics
data).

```
# 1. Configure the token (user secrets keep it out of source)
cd src/FixPortal.Ci.Backend.Api
dotnet user-secrets init
dotnet user-secrets set "GitHub:Token" "<your-read-only-PAT>"
dotnet user-secrets set "GitHub:Owner" "<your-org>"

# 2. Run the API (http profile listens on http://localhost:5049)
dotnet run
```

The snapshot is then at `http://localhost:5049/api/dashboard/snapshot`; any
non-API path redirects to `https://www.fixportal.org/ci`. To run a board UI
against a local API, see [fixportal-ci-frontend](https://github.com/FixPortal/fixportal-ci-frontend).

## Configuration

All settings live in `src/FixPortal.Ci.Backend.Api/appsettings.json` under
`GitHub` and `Dashboard`. The one most forks change is `GitHub:Owner`. Any value
can be overridden by an environment variable using `__` for `:` (e.g.
`GitHub__Token`). The full table — owner, token, refresh cadences, archived/
reusable/CodeQL filters, metrics, merged-PR tracking, and job lanes — is
documented in **[operator-handoff.md](operator-handoff.md#configuration-model)**.

## Deployment

CI deploys to **Azure Container Apps** on every push to `main`. The publish job
builds the image once on Blacksmith, pushes `sha-<full commit SHA>` to GHCR, and
the deploy job imports that exact image into ACR before
`deploy/bicep/main.bicep` ships it. The Bicep template
and workflow carry **no** subscription, registry, or resource identifiers — those
come from GitHub Actions **repository Secrets** (kept as secrets so they are
masked in the public Actions logs), so the template is reusable as-is. One-time
bootstrap (OIDC, secrets), the full secret/variable list, and the custom-domain
dance are all in **[operator-handoff.md](operator-handoff.md#deploying-to-azure)**.

To host elsewhere, the `Dockerfile` is a standard multi-stage build (non-root,
port 8080) that runs on any container platform — only the GitHub token and owner
are required.

## Testing

```
dotnet tool restore
dotnet csharpier check .
dotnet build FixPortal.Ci.Backend.slnx --configuration Release
dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build
```

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Snapshot returns stale data after a workflow run | Refresh worker polls on a 60 s cadence; run completed between polls | Wait up to 60 s, or restart to force an immediate poll |
| Snapshot is empty on startup | First poll not yet complete | Allow ~5 s; endpoint returns `200` with empty `repos` array until first poll finishes |
| `MetricsEnrichmentWorker` logs `git clone` errors | PAT lacks **Contents** read permission | Re-issue PAT with Contents (read) and update `GitHub__Token` |
| PRs not appearing | PAT lacks **Pull requests** read permission | Re-issue PAT with Pull requests (read) and update `GitHub__Token` |
| CORS errors in the browser | Board UI origin not in `AllowedOrigins` | Add origin to `Dashboard:AllowedOrigins` in `appsettings.json` or via `Dashboard__AllowedOrigins__0` env var |
| `401 Unauthorized` from GitHub API | Token expired or revoked | Generate a new fine-grained PAT and update the secret or env var |

## Contributing

PRs welcome. Branch from `main`; CI runs formatting, build, xUnit tests, and
CodeQL on every PR. Stryker runs nightly and on manual dispatch.

```
dotnet tool restore
dotnet csharpier format .
dotnet build FixPortal.Ci.Backend.slnx --configuration Release
dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build
```

Merge style is **rebase-merge** — squash and merge commits are not used.

## License

[Apache-2.0](LICENSE) © 2026 Chris Dowling.

## Appendix

### Container images

The backend image publishes to GHCR on every push to `main`, tagged `:latest`
and `:sha-<full commit SHA>`.

| Image | Pull command |
|---|---|
| Backend API | `docker pull ghcr.io/fixportal/fixportal-ci-backend:latest` |
| Board UI | `docker pull ghcr.io/fixportal/fixportal-ci-frontend:latest` |

### Related repositories

| Repository | Purpose |
|---|---|
| [`FixPortal/fixportal-ci-frontend`](https://github.com/FixPortal/fixportal-ci-frontend) | Open-source board UI — reference snapshot consumer |

### Key endpoints

| Endpoint | Description |
|---|---|
| `GET /api/dashboard/snapshot` | Full org snapshot — workflows, PRs, metrics, job lanes |
| `GET /*` (non-API) | 301 redirect to `https://www.fixportal.org/ci` |
