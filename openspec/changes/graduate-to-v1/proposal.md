## Why

ZipDrive has matured past its V3 prototype phase — 196 tests passing, 8-hour soak test validated, all core subsystems implemented. The "V3" suffix in every project name, namespace, and telemetry identifier is a development artifact that doesn't belong in a shipped product. Simultaneously, there's no versioning infrastructure or release pipeline, making it impossible to produce versioned releases or embed version information in the binary.

## What Changes

- **BREAKING**: Rename all projects, namespaces, solution file, and telemetry identifiers from `ZipDriveV3.*` to `ZipDrive.*` (13 projects, ~94 source files, 3 telemetry meters, 2 activity sources)
- **BREAKING**: Published executable changes from `ZipDriveV3.Cli.exe` to `ZipDrive.exe`
- Add `Directory.Build.props` with centralized `<Version>1.0.0-dev</Version>` for all projects
- Add version display at application startup via `AssemblyInformationalVersionAttribute`
- Create GitHub Actions release workflow triggered by `release-*` tags (e.g., `release-1.0.1`, `release-1.0.1-beta`)
- Update CI workflow to also trigger on pushes to `main` (currently PR-only)
- Clean up all documentation (README.md, CLAUDE.md, src/Docs/) — remove V3 references, fix stale status markers, update commands and paths

## Capabilities

### New Capabilities

- `release-pipeline`: Tag-driven GitHub Actions workflow that builds, tests, publishes a single-file executable, and creates a GitHub Release with version-stamped assets and prerelease detection.
- `version-embedding`: Centralized version management via Directory.Build.props, version override at publish time, and startup version logging.

### Modified Capabilities

- `cli-application`: Executable output name changes from `ZipDriveV3.Cli.exe` to `ZipDrive.exe`. Startup now logs version. OTel meter/source names change.
- `telemetry`: Meter names change from `ZipDriveV3.Caching`/`ZipDriveV3.Zip`/`ZipDriveV3.Dokan` to `ZipDrive.Caching`/`ZipDrive.Zip`/`ZipDrive.Dokan`. ActivitySource names change similarly.

## Impact

- **All source files**: Namespace rename across entire codebase (~94 .cs files, 13 .csproj files, 1 .slnx)
- **CI/CD**: ci.yml updated for new paths + push trigger; new release.yml added
- **Build system**: New Directory.Build.props at repo root
- **Telemetry dashboards**: Any existing Aspire/Grafana dashboards keyed on `ZipDriveV3.*` meter names will need updating
- **Documentation**: README.md, CLAUDE.md, 7 design docs in src/Docs/, openspec archived changes
- **Git history**: Large structural rename — single commit recommended for clean bisectability
