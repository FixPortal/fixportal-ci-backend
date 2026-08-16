# Contributing

Thanks for your interest in improving this project. It is maintained on a
best-effort basis; issues and pull requests are welcome.

## Ground rules

- Be civil. This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
- By contributing, you agree your contributions are licensed under the
  [Apache License 2.0](LICENSE), the same licence as the project.
- Open an issue before a large change so we can agree the approach before you
  invest the time.

## Getting set up

Prerequisites: **.NET 10 SDK** and a fine-grained read-only GitHub PAT (see
[operator-handoff.md](operator-handoff.md#github) for the exact scopes).

```bash
git clone https://github.com/FixPortal/fixportal-ci-backend.git
cd fixportal-ci-backend
dotnet user-secrets init --project src/FixPortal.Ci.Backend.Api
dotnet user-secrets set "GitHub:Token" "<your-read-only-PAT>" --project src/FixPortal.Ci.Backend.Api
dotnet user-secrets set "GitHub:Owner" "<your-org>" --project src/FixPortal.Ci.Backend.Api
dotnet run --project src/FixPortal.Ci.Backend.Api # API on http://localhost:5049
```

## Before you open a PR

Restore the repository-pinned tools, format C#, then run the same build and test
checks required by CI:

```bash
dotnet tool restore
dotnet csharpier format .
dotnet build FixPortal.Ci.Backend.slnx --configuration Release
dotnet test --solution FixPortal.Ci.Backend.slnx --configuration Release --no-build
```

CI uses `dotnet csharpier check .` so it rejects unformatted C# without rewriting
files. Format-on-save through a CSharpier editor extension is recommended but
optional.

## Branches and commits

- Branch from `main` using `feat/<scope>`, `fix/<scope>`, or `chore/<scope>`.
- Write clear, present-tense commit subjects.
- PRs merge via **rebase** — no merge commits, no squash. Keep your branch
  rebased on `main`.

## What makes a good PR

- One focused change per PR.
- Tests for new behaviour or a bug fix that would have caught the regression.
- Keep the service **read-only**: it must never request write scopes or mutate
  anything on GitHub.
- Changes to the `DashboardSnapshot` JSON shape are a published contract shared
  with the frontend — call them out explicitly in the PR description.
