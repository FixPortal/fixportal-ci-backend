# Feature-complete Review Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three confirmed backend correctness/security gaps and make the living operator and architecture documentation match the current service.

**Architecture:** Preserve the existing degradation model and endpoint patterns. Failed review refreshes clear all head-scoped review-derived state; incomplete code-scanning pagination returns unknown; the private admin response adopts the cache headers already used by private IDE endpoints. Documentation changes are factual corrections only.

**Tech Stack:** .NET, ASP.NET Core minimal APIs, xUnit v3, AwesomeAssertions, GitHub REST/GraphQL integrations, Markdown, Docker Compose.

## Global Constraints

- Work only on `reviewer-findings-batch1` in the dedicated review worktree.
- Assert with AwesomeAssertions and match the existing test style.
- Do not change review-connection truncation behavior; it is an acknowledged design choice.
- Do not change secret-scanning pagination; a partial non-zero count cannot produce a false clean result.
- Do not rewrite dated files under `docs/superpowers/specs` or older plans.
- Do not replace diagrams merely to change format. Correct factual text and diagram labels only.
- Do not add dependencies or a new documentation framework.

---

### Task 1: Close the backend correctness, security, and living-document gaps

**Files:**
- Modify/Test: `src/FixPortal.Ci.Backend.Api/Dashboard/Services/DashboardRefreshService.cs` and its existing merge test
- Modify/Test: `src/FixPortal.Ci.Backend.Api/Dashboard/Endpoints/DashboardEndpoints.cs` and `tests/FixPortal.Ci.Backend.Api.Tests/Api/DashboardEndpointTests.cs`
- Modify/Test: `src/FixPortal.Ci.Backend.Api/Integrations/GitHub/GitHubOrgClient.cs` and `tests/FixPortal.Ci.Backend.Api.Tests/Integrations/GitHubCodeScanningTests.cs`
- Modify: `README.md`
- Modify: `docs/architecture/overview.md`
- Modify: `docs/operator-handoff.md`

- [ ] Extend the existing failed-refresh merge test with `ReadyToMerge = true`, prove it fails, then change `WithoutReviewSignals` to clear both `ReviewSignals` and `ReadyToMerge`.
- [ ] Extend the admin snapshot endpoint test to require `Cache-Control: private, no-store` and `Vary: X-Admin-Key`, prove it fails, then set those headers before authorization, matching the IDE endpoint pattern.
- [ ] Add a two-page code-scanning test where page one is full and page two fails, prove the current method returns a misleading partial result, then return `null` for any failed page.
- [ ] Remove the root README YAML frontmatter that GitHub renders as a table.
- [ ] Correct living refresh intervals to 20 seconds and 150 seconds, CORS keys to `Cors:AllowedOrigins`, endpoint descriptions/routes, the active-scanning example, and troubleshooting timing.
- [ ] Document the public projection, private admin endpoint, health endpoint, and IDE v1 snapshot/diagnosis endpoints concisely with their authentication/cache contracts.
- [ ] Correct the living architecture overview for current review/merge/IDE flows and remove brittle generated degree counts without rewriting historical records or changing diagram format.
- [ ] Correct the operator handoff to describe separate backend/frontend images, anonymous pulls of the currently public GHCR images, portable repository-relative links, and support for either a GitHub organization or user owner.
- [ ] Run CSharpier check, the full test suite with NuGet audit disabled only if the private-feed audit endpoint remains unreachable, and a Release build.
- [ ] Commit the completed task locally; do not push.

