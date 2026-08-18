# Operator Handoff

This backend polls the **GitHub Actions API for every repository owned by a
GitHub organization**, normalizes the results into a JSON snapshot, and
serves the API. It is read-only with deep links out to the underlying runs.

The dashboard is two separately published images: this backend API and the
`fixportal-ci-frontend` board UI. Docker Compose runs both; the frontend's nginx
proxies `/api` to the backend over the Compose network. For frontend source
development, use the frontend repository and point it at the backend on
`http://localhost:5049`.

## Configuration model

Settings are bound from configuration. `appsettings.json` supplies the
`GitHub`, `Dashboard`, and `ReviewSignals` defaults; `MergeState`, `Admin`,
`IdeIntegration`, and `Cors` use code defaults unless host configuration sets
them. A host overrides any value with an environment variable whose name is
the config path with `:` replaced by `__` (double underscore) — e.g.
`GitHub:Token` becomes `GitHub__Token`. The Azure deployment uses environment
variables for its secrets, owner, refresh cadence, allowed origins, and production
reviewer list (see **Deploying to Azure**); the remaining settings come from
`appsettings.json`.

| Setting | Default | What it does |
|---|---|---|
| `GitHub:Owner` | `FixPortal` | The GitHub organization whose repositories are enumerated. **The one setting most forks change.** |
| `GitHub:Token` | *(empty)* | Fine-grained read-only PAT (see **GitHub**). Required — the app fails fast at startup if empty. |
| `Dashboard:RefreshSeconds` | `20` | Snapshot refresh cadence. Must be > 0. The collector issues conditional GETs (see **GitHub**), so a tight cadence stays well within the rate budget. |
| `Dashboard:SnapshotPath` | `App_Data/dashboard-snapshot.json` | Last-known-good snapshot, relative to the content root. |
| `Dashboard:ExcludeArchived` | `true` | Skip archived repositories. |
| `Dashboard:IncludeReusable` | `false` | Hide reusable (`_*.yml`) and Dependabot workflows. |
| `Dashboard:IncludeCodeQl` | `true` | Keep CodeQL default-setup workflows in the board. |
| `Dashboard:MetricsEnabled` | `true` | Run the slow Lizard code-metrics worker (NLOC / cyclomatic complexity). |
| `Dashboard:MetricsRefreshSeconds` | `43200` | Metrics cadence (12 h). Each pass shallow-clones every repo. |
| `Dashboard:MergedPrEnabled` | `true` | Track the org's most-recently-merged PR. |
| `Dashboard:MergedPrRefreshSeconds` | `150` | Merged-PR cadence (2.5 min). |
| `Dashboard:JobLanes` | deploys, packages | Named lanes that surface matching workflow **jobs** (by name pattern) as their own status chips. |
| `ReviewSignals:Reviewers` | *(empty)* | Per-reviewer status pills (CodeRabbit, Gitar, CodeQL, …) on each open PR. Empty by default — the feature is off and issues no GitHub requests until reviewers are configured. Full option reference: **[README.md § Review signals](README.md#review-signals-reviewsignals)**. |
| `Cors:AllowedOrigins` | *(empty)* | Origins allowed to read the anonymous public snapshot; configure as `Cors__AllowedOrigins__0`, etc. |
| `Admin:AdminKey` / `IdeIntegration:ApiKey` | *(empty)* | Shared secrets for the private admin snapshot and IDE v1 endpoints. Keep them out of source. |

There is **no hardcoded repository list** — the board auto-discovers every repo
under `GitHub:Owner` and every workflow in each, so new repos and workflows
appear without configuration.

## GitHub

The collector needs a **fine-grained, read-only** Personal Access Token.

1. Create it: GitHub → your avatar → **Settings** → **Developer settings** →
   **Personal access tokens** → **Fine-grained tokens** → **Generate new token**.
2. Scope it:
   - **Resource owner** → the organization that hosts the repos (the
     `GitHub:Owner` value).
   - **Repository access** → **All repositories** (so new repos are picked up
     automatically), or **Only select repositories** to limit the board.
   - **Permissions** → **Repository permissions**, all *Read-only*:
     - **Actions** — workflow-run status (the core signal). Setting this
       auto-adds **Metadata: Read-only**, a mandatory dependency — leave it.
     - **Pull requests** — open PRs and the most-recently-merged PR.
     - **Contents** — `git clone` for the Lizard code-metrics worker.
     - **Code scanning alerts** — open CodeQL alert counts, needed only if
       `ReviewSignals` configures a `CodeScanning` reviewer. Without it that
       reviewer reports "not yet reviewed" (`pending`) on every pull request;
       no other review signal is affected.
   - Leave every other permission at **No access**. A fine-grained PAT has no
     single "read-only" switch; it is read-only purely because every granted
     permission is *Read*. (A classic PAT's nearest scope, `repo`, grants write
     too, so it cannot be made genuinely read-only at this granularity.)
3. Generate and copy the token (shown once).

> **Permission ↔ data mapping.** With **Actions** only, private repos show
> workflow signals but no PRs and no metrics. Add **Pull requests** for PR data
> and **Contents** for metrics. A missing permission degrades gracefully (the
> affected section is empty) rather than blanking the repo.

The dashboard never exposes the token to the browser; all GitHub calls happen
server-side in the background collector.

> **Rate limits & conditional requests.** A fine-grained PAT allows 5000 REST
> requests/hour. The collector caches an ETag per URL and re-validates with
> `If-None-Match`; GitHub answers an unchanged resource with `304 Not Modified`,
> which it does **not** charge against the primary rate limit. A stable org
> therefore costs almost nothing per cycle — only changed resources spend budget
> — which is what makes the 20s `RefreshSeconds` cadence safe. The frontend polls
> the in-memory snapshot (not GitHub) on the same 20s interval, so the browser
> never touches the rate budget.

## Running locally via Docker Compose

Both the frontend and backend can be run locally with
[docker-compose.yml](docker-compose.yml). The current public GHCR images can be
pulled anonymously; no `docker login` or package-read token is required.

### Configure Environment and Run
Once authenticated, configure the collector and start the containers. Both
`GITHUB_TOKEN` and `GITHUB_OWNER` are **required** — the backend validates them at
startup and exits immediately if either is missing, logging
`GitHub:Owner must be configured (e.g. set GitHub__Owner).` The frontend still
starts, so a dead backend with a live UI means these are unset.

The simplest route is a `.env` file, which Compose auto-loads from the project
directory:

```powershell
Copy-Item .env.example .env
# Edit .env: set GITHUB_TOKEN and GITHUB_OWNER
docker compose up -d
```

Or set them in the shell instead:

```powershell
# The classic or fine-grained PAT used by the C# application to read repository metrics and API data
$env:GITHUB_TOKEN = "your_github_pat"  

# The target GitHub organization to query (e.g., "FixPortal")
$env:GITHUB_OWNER = "FixPortal"        

docker compose up -d
```

The board is then at `http://localhost:8082` and the snapshot API at
`http://localhost:5049/api/dashboard/snapshot`.

### Troubleshooting Port Conflicts
Both services publish on `127.0.0.1` only (`127.0.0.1:8082:8080` for the
frontend, `127.0.0.1:5049:8080` for the backend) because the snapshot endpoint is
unauthenticated and must not be offered to the LAN. If either host port is
already taken, change the **host** side of the mapping in
[docker-compose.yml](docker-compose.yml)
— e.g. `"127.0.0.1:8083:8080"` — and keep the `127.0.0.1:` prefix. Do not drop it
to bind on all interfaces.

## Deploying to Azure

The app runs on **Azure Container Apps** (ACA). A push to `main` (or a manual
run of the CI workflow from `main`) builds the app once on a GitHub-hosted
`ubuntu-latest` runner, pushes a
commit-tagged image to GHCR, imports that exact image into an existing Azure
Container Registry, and deploys it into an existing Container Apps managed
environment via `deploy/bicep/main.bicep`.

ACA is used rather than App Service because a fresh subscription ships with **0
App Service compute quota** (which even the Free tier counts against), whereas
Container Apps uses a separate quota model that works without a quota request.

- The image is pulled by a **user-assigned identity** holding **AcrPull** on the
  registry — no registry credentials are stored.
- **`minReplicas: 1`** keeps one replica always running so the in-process
  refresh worker stays alive.
- The GitHub token is a container-app **secret** (`github-token`), written from
  the `DASHBOARD_GH_TOKEN` GitHub secret on each deploy. The snapshot path is
  baked into the image (`/app/data`, ephemeral — it rebuilds within
  `RefreshSeconds` of a restart).

### Deployer-specific configuration (no infra identifiers in source)

The Bicep template and CI workflow carry **no** subscription, resource-group,
registry, or identity literals. The CI deploy reads them from **GitHub Actions
repository Secrets and Variables** (Settings → Secrets and variables → Actions).
The infra identifiers are kept as **Secrets** — not because an RG name or
resource ID is itself sensitive, but so they are **masked in the (public) Actions
logs** rather than echoed in the `az` command lines. Only the public custom
domain is a plain **Variable**. Set them once per deployment:

**Secrets** (masked everywhere in logs):

| Secret | Example | What |
|---|---|---|
| `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | *(GUIDs)* | OIDC login for the deploy identity (set by the bootstrap script). |
| `DASHBOARD_GH_TOKEN` | *(PAT)* | Read-only PAT, written into the container-app secret on each deploy (set by the bootstrap script). |
| `AZURE_RESOURCE_GROUP` | `rg-myapp-prod` | Resource group the container app is deployed into. |
| `ACR_NAME` | `myregistry` | Registry that receives the image imported from GHCR. |
| `ACR_LOGIN_SERVER` | `myregistry.azurecr.io` | Login server the image is pulled from. |
| `ACA_ENVIRONMENT_ID` | `/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.App/managedEnvironments/<env>` | Existing managed environment that hosts the app. |
| `PULL_IDENTITY_ID` | `/subscriptions/<sub>/.../userAssignedIdentities/<id>` | User-assigned identity holding AcrPull. |
| `CUSTOM_DOMAIN_CERT_ID` | `/subscriptions/<sub>/.../managedCertificates/<cert>` *(optional)* | Managed cert for the custom domain (see note below). |

**Variables** (public, not masked):

| Variable | Example | What |
|---|---|---|
| `CUSTOM_DOMAIN_NAME` | `ci.example.org` *(optional)* | Custom domain for the ingress — already public (it is the URL). Leave **unset/empty** to serve on the generated FQDN. |

> **Custom domain.** A managed certificate cannot be provisioned inline in Bicep
> (chicken-and-egg with the binding), so bind the domain once out-of-band with
> `az containerapp hostname bind`, then copy the resulting managed-cert resource
> ID into `CUSTOM_DOMAIN_CERT_ID`. Declaring both in the Bicep means incremental
> deploys **preserve** the binding instead of stripping it. With
> `CUSTOM_DOMAIN_NAME` empty, the binding is skipped entirely.

### One-time setup

> **Bootstrap before the first push to `main`.** The `deploy` job fires on every
> push to `main` — including the merge commit that first lands this pipeline. If
> the bootstrap has not run, that first deploy fails at `azure/login` (no
> secrets). It is harmless and self-recovering, but to avoid a confusing red
> first run, complete the steps below before merging.

1. Authenticate locally: `az login` and `gh auth login`.
2. Create the fine-grained read-only PAT (see **GitHub**).
3. From the repo root, run the bootstrap once (PowerShell 7). It creates a
   resource group, an Entra app with a GitHub OIDC federated credential, grants
   it Contributor on that group, and sets the four OIDC/token repository
   **Secrets** (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
   `DASHBOARD_GH_TOKEN`):

   ```
   ./scripts/bootstrap-azure.ps1 -GitHubToken '<the-PAT>'
   ```

4. Set the remaining infra **Secrets** from the table above (`gh secret set
   ACR_NAME --body '...'`, etc.) and the `CUSTOM_DOMAIN_NAME` **Variable**.
   On Windows, set values that start with `/` (the resource IDs) from
   **PowerShell**, not Git Bash — Bash rewrites a leading slash into a path.
5. Ensure the deploy identity can build into the registry and deploy into the
   target environment's resource group. If the registry / managed environment
   live in a **different** resource group from the one the bootstrap created,
   grant the deploy identity Contributor on that group too:

   ```
   az role assignment create --assignee <AZURE_CLIENT_ID> --role Contributor --scope /subscriptions/<sub>/resourceGroups/<rg>
   ```

6. Push to `main` (or run **Actions → CI → Run workflow**). The `deploy` job
   builds the image, deploys the container app, and probes it.

### Secret rotation

Every runtime secret follows the same shape: a **GitHub Actions secret** is the
source of truth, and the deploy writes it into a **container-app secret** that
backs an environment variable. Rotating any of them is therefore "update the
Actions secret, redeploy".

| Actions secret | Container-app secret | Env var | What it is |
|---|---|---|---|
| `DASHBOARD_GH_TOKEN` | `github-token` | `GitHub__Token` | Read-only PAT the collector polls GitHub with. |
| `CI_ADMIN_KEY` | `admin-key` | `Admin__AdminKey` | Shared key guarding `/api/dashboard/snapshot/admin`. Any client that calls the admin snapshot endpoint **must present the same value** — rotate both together or the admin snapshot starts returning 401. |

To rotate either:

1. Generate the new value (a new read-only PAT, or a new random admin key).
2. `gh secret set DASHBOARD_GH_TOKEN --body '<new-PAT>'` (or `CI_ADMIN_KEY`).
   For the admin key, set the **same** value on every client that calls the
   admin snapshot endpoint in the same change.
3. Re-run the CI workflow on `main`. The deploy rewrites the container-app secret.

> ACA caches secrets on the running revision: if you rotate an Actions secret
> without a full redeploy, restart the revision (`az containerapp revision
> restart`) for the new value to take effect.


## Validation

1. Open the site root (`/`) and confirm the board renders one card per repo in
   the org, with real workflow states.
2. Call the snapshot endpoint and check the status:

   ```
   curl -i https://<fqdn>/api/dashboard/snapshot
   ```

   - `204 No Content` before the first successful refresh, or
   - `200 OK` with a JSON snapshot once a refresh has completed (within
     `Dashboard:RefreshSeconds` of startup).
3. Click a workflow chip to confirm it deep-links to the right GitHub Actions run.

If a card shows "unknown" after the first refresh window, the token most likely
lacks the permission for that data type — recheck the **Permission ↔ data
mapping** note under **GitHub**. If a previously-valid token expires or GitHub is
briefly unreachable, the refresh logs the failure and **keeps the last known
good snapshot** rather than blanking the board; the ageing "updated … ago"
timestamp signals the staleness until the next good refresh.

> `/scalar` and `/openapi` are mapped only in the Development environment, so
> they are not available on a production deployment by design.
