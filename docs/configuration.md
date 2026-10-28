# Configuration reference

Every option the backend binds, with its default. Values come from the Options classes under
`src/FixPortal.Ci.Backend.Api` and the shipped `appsettings.json`; where the two differ, the
`appsettings.json` value is what a default install runs with.

Set any key as an environment variable by replacing `:` with `__` (`GitHub:Owner` becomes
`GitHub__Owner`). Configuration keys are case-insensitive. List items take an index:
`Dashboard__IncludeRepositories__0`, `Dashboard__IncludeRepositories__1`, and objects inside a
list nest the same way (`ReviewSignals__Reviewers__0__Name`).

`docker-compose.yml` forwards only the keys it names (`GitHub__Token`, `GitHub__Owner`,
`Cors__AllowedOrigins__0`, `Dashboard__SnapshotPath`) and has no `env_file`, so a key set only in
`.env` reaches the container only if the compose file references it. See `.env.example`.

## GitHub access

| Key | Default | Notes |
|---|---|---|
| `GitHub:Owner` | `FixPortal` (`appsettings.json`) | Organisation to show. Required, non-blank. Under Compose it must be set explicitly, because an unset `${GITHUB_OWNER}` substitutes an empty string. |
| `GitHub:Token` | empty | Fine-grained PAT. Required in every mode. See `operator-handoff.md` for scopes. |
| `GitHubApp:AppId` | none | GitHub App mode. Needs `GitHubApp:PrivateKeyPem` as well. |
| `GitHubApp:PrivateKeyPem` | none | The key contents, not a path. Startup fails if the value does not contain `PRIVATE KEY`. |
| `GitHubApp:InstallationId` | none | Optional. When omitted it is discovered from the organisation installation. |

## Dashboard

| Key | Default | Notes |
|---|---|---|
| `Dashboard:SnapshotPath` | `App_Data/dashboard-snapshot.json` | The container image sets `/app/data/dashboard-snapshot.json`. |
| `Dashboard:RefreshSeconds` | `20` | Board refresh cadence. Must be greater than zero. The shipped Azure Bicep sets 30. |
| `Dashboard:ExcludeArchived` | `true` | |
| `Dashboard:IncludeRepositories`, `ExcludeRepositories`, `IncludeTopics`, `ExcludeTopics` | empty lists | Simple wildcard patterns. A blank entry, or one with leading or trailing whitespace, stops startup. |
| `Dashboard:IncludeReusable` | `false` | |
| `Dashboard:IncludeCodeQl` | `true` | |
| `Dashboard:RunHistoryPageSize` | `30` | |
| `Dashboard:MetricsEnabled` | `true` | Code metrics through Lizard. |
| `Dashboard:MetricsRefreshSeconds` | `43200` | Twelve hours. |
| `Dashboard:MetricsWorkDirectory` | empty | Empty means a temporary subdirectory. |
| `Dashboard:MergedPrEnabled` | `true` | |
| `Dashboard:MergedPrRefreshSeconds` | `150` in `appsettings.json` (`300` if the key is absent) | |
| `Dashboard:JobLanes` | the `deploys` and `packages` lanes in `appsettings.json` | Per lane: `Key` and `Label` (required), `Enabled` (`true`), `RefreshSeconds` (`300` if absent, `150` in the shipped lanes), `Patterns` (empty), `MaxRunsToScan` (`30`, must be greater than zero). |

## Merge state and review signals

| Key | Default | Notes |
|---|---|---|
| `MergeState:Enabled` | `true` | |
| `MergeState:RefreshSeconds` | `120` | |
| `ReviewSignals:Enabled` | `true` | |
| `ReviewSignals:RefreshSeconds` | `150` | |
| `ReviewSignals:ReserveBudgetPoints` | `1000` | |
| `ReviewSignals:ExcludedAuthors` | four bot logins in `appsettings.json` | |
| `ReviewSignals:Reviewers` | empty list, so the feature is idle | Per reviewer: `Name` (required), `BotLogin` (required when `Source` is `ReviewThreads`, the default, or when `CommentsCountAsParticipation` is `true`; it must have no leading or trailing whitespace, and startup fails otherwise), `RequiredLabel`, `WaivedLabel`, `WaivedBodyDirective`, `CommentsCountAsParticipation`, `CodeScanningCheckCountsAsParticipation`, `PublicOnly` (all `false`), `Source` (`ReviewThreads`). The README section on review signals describes the semantics. |

## Admin and integration

| Key | Default | Notes |
|---|---|---|
| `Admin:AdminKey` | empty | Empty fails closed: the admin and merge endpoints refuse every caller. When set it must be at least 16 characters. Clients send it as `X-Admin-Key`. |
| `Admin:ExposePrivateToGuests` | `false` | Security-sensitive. When `true`, the unauthenticated `/api/dashboard/snapshot` returns private repositories too. Leave it unset on any internet-reachable deployment. |
| `IdeIntegration:ApiKey` | empty | Empty makes the IDE endpoints return 401. Key rules are in `operator-handoff.md`. |
| `Cors:AllowedOrigins` | none | An empty list blocks all cross-origin calls. |
