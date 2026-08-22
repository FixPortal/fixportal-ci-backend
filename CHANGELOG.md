# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Configurable case-insensitive repository-name and GitHub-topic filters for
  dashboard sweeps. Name and topic include lists intersect, exclusions win, and
  empty lists preserve the default org-wide behaviour.
- Authentication documentation for both outbound modes - GitHub App (what
  production runs) and fine-grained PAT - covering which settings select which
  mode, the App permissions each reviewer source needs, and why a PAT degrades
  every review pill rather than only the check-run-derived ones.
- Coverage reporting, restored after the OSS cut removed it: `dotnet-coverage`
  wraps the existing test command and a step summary reports line and branch
  rates. Reported only, no threshold enforced.
- Startup validation rejecting repository-filter patterns with leading or
  trailing whitespace, which previously passed validation and then matched
  nothing, emptying the board while startup stayed green.

### Changed

- Hardened the public-build regression guard to reject any NuGet
  `packageSourceCredentials` block, regardless of feed host or credential name.
- The review-policy guard now parses workflows structurally instead of matching
  line-anchored patterns. Those patterns were bypassable by ordinary block-style
  YAML - a `permissions:` key with `write-all` on the following line, or a split
  `uses:` - both of which resolve normally and passed the guard green.
- The guard workflow and its checker are themselves tiered HIGH. The required
  status context is produced by a pull request's own copy of the workflow, so
  replacing its assertions with a no-op previously merged green.

### Fixed

- A persisted snapshot is no longer restored under repository filters different
  from the ones that produced it. Restarting after tightening a filter served the
  older, wider repository set - on the anonymous public endpoint too, and with no
  time bound while GitHub was unreachable.
- A GraphQL response carrying `data: null` no longer erases the rate-limit
  observation. The reserve guard fails open on a missing reading, so a sweep could
  spend past the configured floor.
- The merge-state worker reports when a sweep stops at the reserve floor instead
  of returning silently, which was indistinguishable from a broken sweep once
  ready-to-merge verdicts began to decay.
- Repository-count logging separates archived exclusions from name/topic filter
  exclusions, and a repository whose payload omits `topics` while topic filters are
  configured is now reported rather than silently escaping the exclude gate.
