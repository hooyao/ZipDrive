## Context

ZipDrive has been developed under the name "ZipDriveV3" across all projects, namespaces, solution files, and telemetry identifiers. With 196 tests passing and an 8-hour soak test validated, the project is ready to ship as a 1.0 product. There is currently no versioning infrastructure (no `Directory.Build.props`, no `<Version>` in any project), no release pipeline, and CI only triggers on PRs.

The rename touches ~150 files across code, configuration, and documentation. The project uses .NET Central Package Management and clean architecture with 13 projects (6 src + 7 test).

## Goals / Non-Goals

**Goals:**
- Remove all "V3" references from project names, namespaces, solution file, telemetry, and documentation
- Establish version 1.0.0 as the starting version with centralized management
- Embed version in the published binary and log it at startup
- Create a tag-driven release workflow that produces versioned GitHub Releases
- Update CI to also run on pushes to main
- Clean up stale documentation (fix outdated status markers, commands, paths)

**Non-Goals:**
- Changing any functional behavior (caching, ZIP reading, Dokan adapter, etc.)
- Migrating to a different .NET version (staying on .NET 10)
- Adding new features or tests beyond version logging
- Updating openspec archived changes (historical artifacts, leave as-is)

## Decisions

### 1. Rename strategy: single mechanical find-replace

**Decision**: Use `ZipDriveV3` → `ZipDrive` as a straight text replacement across all file contents, then rename directories and files.

**Rationale**: The "V3" prefix is used consistently everywhere. A simple find-replace is reliable and auditable. No partial renames or aliasing needed since this is a clean break with no backward compatibility concerns.

**Alternatives considered**:
- Gradual rename with backward-compatible aliases — unnecessary complexity for a pre-1.0 project with no external consumers.

### 2. Version source of truth: tag-driven with dev fallback

**Decision**: `Directory.Build.props` sets `<Version>1.0.0-dev</Version>` as the default. The release workflow overrides this with `-p:Version=X.Y.Z` parsed from the git tag `release-X.Y.Z`.

**Rationale**: Tag-driven versioning means no file edits are needed to cut a release — just push a tag. The `-dev` suffix in the base version makes local/CI builds clearly distinguishable from releases. .NET's `-p:Version` MSBuild override is the standard mechanism and sets `AssemblyVersion`, `FileVersion`, and `InformationalVersion` simultaneously.

**Alternatives considered**:
- Manual version bumps in `Directory.Build.props` per release — error-prone, requires a commit before every release tag.
- GitVersion / Nerdbank.GitVersioning — too heavy for this project's needs. Adds complexity and dependency for something a 5-line shell script handles.

### 3. Executable naming: AssemblyName override in CLI project

**Decision**: Add `<AssemblyName>ZipDrive</AssemblyName>` only in the CLI project's `.csproj`. The project directory stays `ZipDrive.Cli/` (clean architecture convention) but the output binary is `ZipDrive.exe`.

**Rationale**: Users interact with `ZipDrive.exe`, not `ZipDrive.Cli.exe`. The `.Cli` suffix is an internal organizational concern. The `<AssemblyName>` override is the standard .NET mechanism for this.

### 4. Release tag format: `release-X.Y.Z[-prerelease]`

**Decision**: Tags follow the pattern `release-1.0.0`, `release-1.0.1-beta`, `release-2.0.0-rc.1`. The workflow parses the version by stripping the `release-` prefix.

**Rationale**: The `release-` prefix clearly communicates intent (vs arbitrary tags). SemVer prerelease suffixes (`-beta`, `-rc.1`) are passed through to the .NET version and GitHub Release. The workflow auto-detects prerelease by checking for a hyphen in the version string.

### 5. Startup version logging: AssemblyInformationalVersionAttribute

**Decision**: Read version at startup via `Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()` and log it via Serilog.

**Rationale**: `InformationalVersion` is the richest version field — it includes prerelease suffixes and build metadata. It's automatically set by the `<Version>` MSBuild property. No custom build steps or source generation needed.

### 6. Documentation cleanup: fix stale content alongside rename

**Decision**: While renaming V3 references, also fix stale status markers and outdated roadmap items in design docs. Don't update openspec archived changes (historical record).

**Rationale**: Stale docs saying "Status: Proposed" for implemented features undermine documentation trust. The rename pass already touches every doc, so fixing staleness has zero marginal cost.

### 7. Telemetry meter/source names: rename to match project

**Decision**: Rename `ZipDriveV3.Caching` → `ZipDrive.Caching`, `ZipDriveV3.Zip` → `ZipDrive.Zip`, `ZipDriveV3.Dokan` → `ZipDrive.Dokan` for both Meters and ActivitySources.

**Rationale**: These are string identifiers in source code. They should match the product name. No backward compatibility concerns since this is pre-1.0 with no external dashboard integrations in production.

## Risks / Trade-offs

**[Large git diff]** → The rename produces a massive diff touching ~150 files. Mitigation: Execute as a single atomic commit for clean bisectability. Review by diffstat (renames should show as moves, not deletions+additions) and verify build+tests pass.

**[Directory rename on Windows]** → Git on Windows can struggle with case-sensitive or large directory renames. Mitigation: Use `git mv` for each directory to ensure proper rename tracking. Verify `git status` shows renames, not delete+add pairs.

**[Broken references after rename]** → ProjectReference paths, solution entries, or namespace imports could be missed. Mitigation: Build must pass after rename. Test suite (196 tests) validates functional correctness. CI workflow validates the full pipeline.

**[Stale external references]** → Any external tooling, scripts, or documentation outside this repo referencing `ZipDriveV3` will break. Mitigation: This is pre-1.0 with no known external consumers. The README and CLAUDE.md (primary entry points) will be updated.
