# API reference

Every HTTP endpoint the backend serves, with its authentication, status codes and response shape.
Facts are taken from `Dashboard/Endpoints/DashboardEndpoints.cs`, `Ide/IdeEndpoints.cs`,
`Ide/IdeSnapshotContracts.cs` and `Dashboard/Model/DashboardModels.cs`. JSON property names are
camelCase. Instants are ISO-8601 UTC strings. Captured example payloads live in
`contracts/dashboard-snapshot.v1.json` (public) and `contracts/dashboard-snapshot.admin.v1.json`
(admin).

| Endpoint | Authentication | Purpose |
|---|---|---|
| `GET /api/dashboard/snapshot` | none | Public snapshot of the organisation |
| `GET /api/dashboard/snapshot/admin` | `X-Admin-Key` | Full snapshot including private repositories |
| `POST /api/dashboard/merge` | `X-Admin-Key` | Rebase-merge one pull request |
| `GET /api/health` | none | GitHub credential health |
| `GET /api/ide/v1/snapshot` | `X-CI-IDE-Key` | Compact run snapshot for the IDE integration |
| `GET /api/ide/v1/repositories/{repository}/runs/{runId}/diagnosis` | `X-CI-IDE-Key` | Failure text excerpt for one run |

Keys are compared in constant time. An unset key fails closed: every request to a keyed endpoint
is refused with `401`. See [configuration.md](configuration.md) for `Admin:AdminKey`,
`Admin:ExposePrivateToGuests` and `IdeIntegration:ApiKey`.

## Public snapshot

`GET /api/dashboard/snapshot`

- `200` with the snapshot, or `204` with no body until the first snapshot exists.
- Private repositories are removed on the server before responding, unless
  `Admin:ExposePrivateToGuests` is `true`.

Top-level fields: `refreshedAt`, `org`, `repositories`, `summary` (a list of `{ key, count }`),
`lastMergedPr`, `ciTrend` and `publicCiTrend` (hourly buckets of `{ bucketStart, state }`, where
`state` is `NoData`, `Passing` or `Failing`; a bucket can be carried forward from an earlier
snapshot during a degraded refresh).

Each repository has `name`, `htmlUrl`, `private`, `workflows`, `pullRequests`, `metrics`,
`deploys`, `packages` and `lastMergedPr`.

## Admin snapshot

`GET /api/dashboard/snapshot/admin`

Same shape as the public snapshot, with private repositories included.

- `401` when `X-Admin-Key` is missing, wrong, or `Admin:AdminKey` is not configured.
- `204` until the first snapshot exists, otherwise `200`.
- Sent with `Cache-Control: private, no-store` and `Vary: X-Admin-Key`.

## Merge

`POST /api/dashboard/merge` with a JSON body and the `X-Admin-Key` header:

```json
{ "repo": "my-repo", "pullNumber": 42, "headSha": "<head commit sha>" }
```

| Status | Body | Meaning |
|---|---|---|
| `200` | `{ "merged": true, "sha": "..." }` | Merged. |
| `400` | `{ "error": "..." }` | Body unreadable, `pullNumber` not above zero, or `headSha` blank. |
| `401` | `{ "error": "Unauthorized." }` | Missing or wrong key, or no key configured. |
| `404` | `{ "error": "Repository not found." }` | The repository is not in the current snapshot. |
| `409` | `{ "error": "..." }` | GitHub refused the merge (it answered `405` or `409`). The cached verdict for that pull request becomes not mergeable, so the board shows it straight away. |
| `502` | `{ "error": "..." }` | GitHub could not be reached, answered another error, or did not report the pull request as merged. |
| `503` | `{ "error": "No dashboard snapshot available yet." }` | No snapshot to look the repository up in. |

The backend asks GitHub for a rebase merge and passes `headSha` as the merge's `sha`, so GitHub
refuses if the pull request has moved on since the caller saw it. Requests for the same pull
request are handled one at a time. The response is sent with `Cache-Control: private, no-store`
and `Vary: X-Admin-Key`. `operator-handoff.md` lists the token scope the merge needs
(`Contents: Read and write`).

## Health

`GET /api/health` is unauthenticated.

- `200` with `{ "status": "Healthy" }`.
- `503` with `{ "status": "Degraded", "error": "GitHub credential check failing" }` while the last
  GitHub call failed authentication. The message is deliberately generic, because the raw error
  contains the failing request URL and could expose a private repository name. The detail is in
  the server log.

## IDE snapshot

`GET /api/ide/v1/snapshot` with the `X-CI-IDE-Key` header.

- `401` without a valid key, `204` until the first snapshot exists.
- `200` with `application/json`:

| Field | Meaning |
|---|---|
| `schemaVersion` | `1` |
| `snapshotId` | `sha256:` and a hash of the content, excluding `snapshotId` and `observedAt`, so it changes only when the content does |
| `observedAt` | When the snapshot was produced |
| `organization` | The GitHub organisation |
| `repositories` | `{ name, private, workflows }`, where each workflow is `{ file, recentRuns }` and each run is `{ runId, attempt, headSha, branch, event, status, conclusion, url, updatedAt }` |

Each workflow lists at most `Dashboard:RunHistoryPageSize` recent runs (default 30).

The response carries a weak `ETag` of `W/"<snapshotId>"`. Send it back as `If-None-Match` and an
unchanged snapshot answers `304`. Responses are sent with `Cache-Control: no-store` and
`Vary: X-CI-IDE-Key`.

## Run diagnosis

`GET /api/ide/v1/repositories/{repository}/runs/{runId}/diagnosis` with the `X-CI-IDE-Key` header.
`runId` must be a number. The run must be in the current snapshot.

| Status | Body | Meaning |
|---|---|---|
| `200` | `{ schemaVersion, repository, runId, attempt, headSha, textSha256, truncated, excerpt }` | An excerpt of the run's log text. `repository` is `owner/name`, `textSha256` is a SHA-256 of the text that was read, and `truncated` says the excerpt is shorter than that text. |
| `401` | none | Missing or wrong key. |
| `404` | `{ "error": "Diagnosis is unavailable." }` | The run cannot be matched to exactly one repository and run in the snapshot, or no diagnosis could be read. |
| `502` | `{ "error": "Diagnosis provider response was rejected." }` | The provider answered something the backend refused to use. |
| `504` | `{ "error": "Diagnosis provider timed out." }` | The provider did not answer in time. |

At most three diagnosis reads run at once. Each read can buffer up to 16 MB of body and 32 MB when
expanded on a single-replica process, so further requests wait their turn.
