# CSharpier Standards Pilot Design

## Goal

Pilot CSharpier in `fixportal-ci-backend` as the single owner of C# whitespace,
wrapping, and layout. Keep `FixPortal.CodeStyle` responsible for semantic style,
naming, analyzer selection, and diagnostic severity.

Success means developers see the readability improvement in normal work, every
environment produces identical formatting, and CI rejects unformatted C# without
making ordinary builds mutate source files.

## Ownership boundary

| Concern | Owner |
| --- | --- |
| C# whitespace, wrapping, and layout | CSharpier |
| Semantic code style and naming | `FixPortal.CodeStyle` |
| Compiler, security, nullable, disposal, and Sonar diagnostics | `FixPortal.CodeStyle` |
| Indentation, line endings, and print width inputs | Repository `.editorconfig` |
| Enforcement | Explicit CI checks |

`IDE0055` will be disabled because its whitespace rules can conflict with
CSharpier. Other IDE and analyzer diagnostics remain unchanged.

The agreed C# configuration is:

```ini
[*.cs]
end_of_line = crlf
indent_style = space
indent_size = 4
max_line_length = 120
dotnet_diagnostic.IDE0055.severity = none
```

CSharpier's XML formatter is outside the pilot, so `xmlWhitespaceSensitivity`
is not configured.

## Pilot changes

1. Add CSharpier `1.3.0` to the existing repository-local .NET tool manifest.
2. Keep configuration in `.editorconfig`: four-space C# indentation, CRLF line
   endings, and an explicit 120-character print width. Add the temporary local
   override `dotnet_diagnostic.IDE0055.severity = none` there until the shared
   package owns it.
3. Add `.csharpierignore` entries for `*.csproj`, `*.props`, `*.targets`, `*.xml`,
   `*.config`, `*.slnx`, `*.xaml`, and `*.axaml`. The first rollout formats C# only.
4. Add `dotnet tool restore` and `dotnet csharpier check .` to the backend CI job.
5. Run CSharpier once and commit the formatting as an isolated, mechanical commit.
6. Document `dotnet csharpier format .` in `CONTRIBUTING.md` as the manual command.
   Editor format-on-save remains recommended but optional.

No pre-commit hook or `CSharpier.MsBuild` package will be added. CI is the single
mandatory gate; Debug and Release builds will never rewrite source files.

## Execution flow

Developer formatting:

```text
dotnet tool restore -> dotnet csharpier format . -> build and test
```

Pull-request validation:

```text
checkout -> restore tools -> CSharpier check -> restore/build/test
```

An unformatted file, syntax error, or CSharpier validation failure produces a
non-zero exit code and fails CI. CI does not modify files.

## Validation

The pilot is complete when all of these pass from a clean checkout:

- `dotnet tool restore`
- `dotnet csharpier check .`
- `dotnet build FixPortal.Ci.Backend.slnx --configuration Release`
- `dotnet test FixPortal.Ci.Backend.slnx --configuration Release --no-build`
- A second formatting run produces no diff.

The formatting commit must contain whitespace-only C# changes. Generated files,
XML, JSON, YAML, Bicep, and Markdown are outside this pilot.

## Estate-wide follow-on

After the pilot has survived ordinary development without formatter conflicts:

1. Add `dotnet_diagnostic.IDE0055.severity = none` to `FixPortal.CodeStyle` and
   release a new package version, then remove the pilot's local override.
2. Trim its consumer `.editorconfig` template so CSharpier owns C# layout settings.
3. Add the pinned local tool and CI check to the standard repository scaffold.
4. Roll out repository by repository, using one isolated formatting commit in each.

The CSharpier version remains deliberately pinned. Version upgrades are reviewed
and applied explicitly because printer changes can create estate-wide diffs.
