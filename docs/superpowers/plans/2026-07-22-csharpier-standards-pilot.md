# CSharpier Standards Pilot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CSharpier the deterministic C# formatter for `fixportal-ci-backend`, with a pinned local tool, an explicit CI gate, and one isolated mechanical formatting commit.

**Architecture:** `FixPortal.CodeStyle` continues to own semantic style and analyzer diagnostics; CSharpier owns only C# whitespace, wrapping, and layout. Repository-local EditorConfig values configure the printer, `.csharpierignore` excludes XML-family files, and CI calls the pinned tool explicitly rather than coupling formatting to MSBuild.

**Tech Stack:** .NET 10 local tools, CSharpier 1.3.0, EditorConfig, GitHub Actions, PowerShell, Git.

## Global Constraints

- Pilot only in `fixportal-ci-backend`; do not modify or release the separate `FixPortal.CodeStyle` repository.
- Pin CSharpier exactly at `1.3.0` in `.config/dotnet-tools.json`.
- Format C# only; exclude project, solution, configuration, and other XML-family files.
- Use spaces, indent size `4`, print width `120`, and `crlf` line endings.
- Set `dotnet_diagnostic.IDE0055.severity = none`; retain every other analyzer rule.
- Do not add `CSharpier.MsBuild`, a pre-commit hook, or a mandatory editor extension.
- Keep the first repository-wide formatting change in its own commit containing only `.cs` files.
- CI must check formatting and must never rewrite source files.

## File Map

- Modify `.config/dotnet-tools.json`: declare the pinned CSharpier local tool.
- Modify `.editorconfig`: record the agreed C# printer inputs and disable conflicting `IDE0055` whitespace enforcement.
- Create `.csharpierignore`: keep XML-family files outside the pilot.
- Mechanically modify `src/**/*.cs` and `tests/**/*.cs`: CSharpier output only, with no hand edits.
- Modify `.github/workflows/ci.yml`: restore the local tools and reject unformatted C#.
- Modify `CONTRIBUTING.md`: document the local formatting workflow.
- Reference only `docs/superpowers/specs/2026-07-22-csharpier-standards-design.md`: do not change the approved design during implementation.

---

### Task 1: Pin and configure the formatter

**Files:**

- Modify: `.config/dotnet-tools.json:1-13`
- Modify: `.editorconfig:28-42`
- Create: `.csharpierignore`

**Interfaces:**

- Consumes: .NET's repository-local tool manifest and CSharpier's EditorConfig support.
- Produces: the command `dotnet csharpier`, fixed at version `1.3.0`, configured for C# at 120 columns with XML-family inputs ignored.

- [ ] **Step 1: Confirm a clean starting point**

Run:

```powershell
git status --short
```

Expected: no output. Stop before staging anything if unrelated changes are present.

- [ ] **Step 2: Verify that CSharpier is not yet declared locally**

Run:

```powershell
dotnet tool run csharpier --version
```

Expected: non-zero exit with a message stating that no manifest tool has the command `csharpier`.

- [ ] **Step 3: Add CSharpier to the tool manifest**

Make `.config/dotnet-tools.json` exactly:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "csharpier": {
      "version": "1.3.0",
      "commands": [
        "csharpier"
      ],
      "rollForward": false
    },
    "dotnet-stryker": {
      "version": "4.16.0",
      "commands": [
        "dotnet-stryker"
      ],
      "rollForward": false
    }
  }
}
```

- [ ] **Step 4: Add the agreed C# settings to EditorConfig**

Keep the existing `[*.cs]` section and make its opening settings read:

```ini
[*.cs]
end_of_line = crlf
indent_size = 4
indent_style = space
tab_width = 4
max_line_length = 120

# CSharpier owns C# whitespace and wrapping. Keep this local override until
# FixPortal.CodeStyle publishes the same setting in its global config.
dotnet_diagnostic.IDE0055.severity = none
```

Leave the existing `IDE0011` and `IDE0049` formatter-compatibility entries and all remaining formatter keys unchanged during the pilot.

- [ ] **Step 5: Exclude XML-family files**

Create `.csharpierignore` with exactly:

```gitignore
*.csproj
*.props
*.targets
*.xml
*.config
*.slnx
*.xaml
*.axaml
```

These are gitignore-style patterns without a slash, so they apply recursively.

- [ ] **Step 6: Restore the pinned tools**

Run:

```powershell
dotnet tool restore
```

Expected: both `csharpier` and `dotnet-stryker` restore successfully.

- [ ] **Step 7: Verify the selected CSharpier version**

Run:

```powershell
dotnet csharpier --version
```

Expected: output contains `1.3.0`.

- [ ] **Step 8: Prove that the existing C# is not yet formatted**

Run:

```powershell
dotnet csharpier check .
```

Expected: exit code `1` with one or more unformatted `.cs` paths. No `.csproj`, `.props`, `.targets`, `.xml`, `.config`, `.slnx`, `.xaml`, or `.axaml` path may appear.

- [ ] **Step 9: Check the configuration diff**

Run:

```powershell
git diff --check
```

Expected: exit code `0` and no output.

- [ ] **Step 10: Stage the formatter configuration**

Run:

```powershell
git add .config/dotnet-tools.json .editorconfig .csharpierignore
```

- [ ] **Step 11: Commit the formatter configuration**

Run:

```powershell
git commit -m "chore: configure CSharpier"
```

Expected: one commit containing only the manifest, EditorConfig, and ignore file.

---

### Task 2: Apply the mechanical C# formatting

**Files:**

- Modify mechanically: `src/**/*.cs`
- Modify mechanically: `tests/**/*.cs`

**Interfaces:**

- Consumes: the pinned `dotnet csharpier` command and repository configuration from Task 1.
- Produces: a CSharpier-clean C# baseline with no semantic or non-C# changes.

- [ ] **Step 1: Format the repository**

Run:

```powershell
dotnet csharpier format .
```

Expected: CSharpier reports the `.cs` files it changed and completes without syntax-tree validation failures.

- [ ] **Step 2: Inspect the changed paths**

Run:

```powershell
git diff --name-only
```

Expected: every path ends in `.cs` and lives under `src/` or `tests/`. Stop if any configuration, documentation, XML-family, generated, or runtime file changed.

- [ ] **Step 3: Review the mechanical diff summary**

Run:

```powershell
git diff --stat
```

Expected: only formatting churn in C# files; no new or deleted application files.

- [ ] **Step 4: Verify formatting**

Run:

```powershell
dotnet csharpier check .
```

Expected: exit code `0` with no unformatted files.

- [ ] **Step 5: Build the formatted solution**

Run:

```powershell
dotnet build FixPortal.Ci.Backend.slnx --configuration Release
```

Expected: build succeeds with `0` warnings and `0` errors.

- [ ] **Step 6: Run the tests without rebuilding**

Run:

```powershell
dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build
```

Expected: all tests pass.

- [ ] **Step 7: Stage only the mechanical C# changes**

Run:

```powershell
git add src tests
```

- [ ] **Step 8: Run the formatter a second time**

Run:

```powershell
dotnet csharpier format .
```

Expected: no files are reformatted.

- [ ] **Step 9: Prove the second pass was idempotent**

Run:

```powershell
git diff --exit-code
```

Expected: exit code `0`; all formatting changes remain staged with no new unstaged diff.

- [ ] **Step 10: Reconfirm the staged file boundary**

Run:

```powershell
git diff --cached --name-only
```

Expected: every staged path ends in `.cs` and lives under `src/` or `tests/`.

- [ ] **Step 11: Commit the mechanical baseline**

Run:

```powershell
git commit -m "style: format C# with CSharpier"
```

Expected: one formatting-only commit, separate from configuration and CI changes.

---

### Task 3: Enforce formatting in CI and document local use

**Files:**

- Modify: `.github/workflows/ci.yml:34-44`
- Modify: `CONTRIBUTING.md:26-34`

**Interfaces:**

- Consumes: the clean C# baseline and pinned local tool from Tasks 1 and 2.
- Produces: a non-mutating pull-request formatting gate and a matching contributor workflow.

- [ ] **Step 1: Add the CI formatting gate**

Insert these steps immediately after `Set up .NET` and before the existing solution `Restore` step in `.github/workflows/ci.yml`:

```yaml
      - name: Restore tools
        run: dotnet tool restore

      - name: Check C# formatting
        run: dotnet csharpier check .
```

Do not add CSharpier to the `publish` or `deploy` jobs; the `backend` job is already their required upstream gate.

- [ ] **Step 2: Replace the contributor pre-PR instructions**

Replace the existing `Before you open a PR` section in `CONTRIBUTING.md` with:

````markdown
## Before you open a PR

Restore the repository-pinned tools, format C#, then run the same build and test
checks required by CI:

```bash
dotnet tool restore
dotnet csharpier format .
dotnet build FixPortal.Ci.Backend.slnx --configuration Release
dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build
```

CI uses `dotnet csharpier check .` so it rejects unformatted C# without rewriting
files. Format-on-save through a CSharpier editor extension is recommended but
optional.
````

- [ ] **Step 3: Run the same formatter check CI will run**

Run:

```powershell
dotnet csharpier check .
```

Expected: exit code `0` with no unformatted files.

- [ ] **Step 4: Check the workflow and documentation diff**

Run:

```powershell
git diff --check
```

Expected: exit code `0` and no whitespace errors.

- [ ] **Step 5: Inspect the task boundary**

Run:

```powershell
git diff --name-only
```

Expected: exactly `.github/workflows/ci.yml` and `CONTRIBUTING.md`.

- [ ] **Step 6: Stage CI and documentation**

Run:

```powershell
git add .github/workflows/ci.yml CONTRIBUTING.md
```

- [ ] **Step 7: Commit the enforcement**

Run:

```powershell
git commit -m "ci: enforce CSharpier formatting"
```

Expected: one commit containing only CI and contributor documentation.

---

### Task 4: Run the clean-checkout-equivalent quality gate

**Files:**

- Verify only; no files should change.

**Interfaces:**

- Consumes: all three implementation commits.
- Produces: evidence that the repository restores, formats, builds, and tests cleanly with no working-tree residue.

- [ ] **Step 1: Restore repository-local tools**

Run:

```powershell
dotnet tool restore
```

Expected: CSharpier `1.3.0` and Stryker restore successfully.

- [ ] **Step 2: Check the C# baseline**

Run:

```powershell
dotnet csharpier check .
```

Expected: exit code `0` with no unformatted files.

- [ ] **Step 3: Restore solution packages**

Run with the repository's required `GITHUB_PACKAGES_TOKEN` already present in the environment:

```powershell
dotnet restore FixPortal.Ci.Backend.slnx
```

Expected: restore succeeds, including `FixPortal.CodeStyle` from GitHub Packages.

- [ ] **Step 4: Build exactly as CI does**

Run:

```powershell
dotnet build FixPortal.Ci.Backend.slnx --configuration Release --no-restore
```

Expected: build succeeds with `0` warnings and `0` errors.

- [ ] **Step 5: Test exactly as CI does**

Run:

```powershell
dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build --logger "trx;LogFileName=test-results.trx" --results-directory ./TestResults
```

Expected: all tests pass and `TestResults/test-results.trx` is produced in the ignored test-output directory.

- [ ] **Step 6: Verify repository cleanliness**

Run:

```powershell
git status --short
```

Expected: no output.

## References

- Approved design: `docs/superpowers/specs/2026-07-22-csharpier-standards-design.md`
- CSharpier installation: <https://csharpier.com/docs/Installation>
- CSharpier configuration: <https://csharpier.com/docs/Configuration>
- CSharpier ignore syntax: <https://csharpier.com/docs/Ignore>
- CSharpier CI usage: <https://csharpier.com/docs/ContinuousIntegration>
- CSharpier and analyzer coexistence: <https://csharpier.com/docs/IntegratingWithLinters>
