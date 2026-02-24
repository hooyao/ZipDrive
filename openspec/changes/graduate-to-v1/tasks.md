## 1. Project Rename (ZipDriveV3 → ZipDrive)

- [x] 1.1 Rename all 13 project directories from `ZipDriveV3.*` to `ZipDrive.*` using `git mv` (6 src + 7 test directories)
- [x] 1.2 Rename all 13 `.csproj` files within the renamed directories from `ZipDriveV3.*.csproj` to `ZipDrive.*.csproj` using `git mv`
- [x] 1.3 Rename solution file from `ZipDriveV3.slnx` to `ZipDrive.slnx` using `git mv`
- [x] 1.4 Update all project paths inside `ZipDrive.slnx` to reference the new directory/file names
- [x] 1.5 Update all `<ProjectReference>` paths in all 13 `.csproj` files to reference new directory/file names
- [x] 1.6 Find-replace all `namespace ZipDriveV3` → `namespace ZipDrive` and `using ZipDriveV3` → `using ZipDrive` across all `.cs` files
- [x] 1.7 Update telemetry meter names in source code: `ZipDriveV3.Caching` → `ZipDrive.Caching`, `ZipDriveV3.Zip` → `ZipDrive.Zip`, `ZipDriveV3.Dokan` → `ZipDrive.Dokan` (in `CacheTelemetry.cs`, `ZipTelemetry.cs`, `DokanTelemetry.cs`)
- [x] 1.8 Update `ActivitySource` names similarly in telemetry classes
- [x] 1.9 Update `.AddMeter()` and `.AddSource()` calls in `Program.cs` to use new meter/source names
- [x] 1.10 Verify build passes: `dotnet build ZipDrive.slnx`
- [x] 1.11 Verify all tests pass: `dotnet test`

## 2. Version Embedding

- [x] 2.1 Create `Directory.Build.props` at repo root with `<Version>1.0.0-dev</Version>` and `<Product>ZipDrive</Product>`
- [x] 2.2 Add `<AssemblyName>ZipDrive</AssemblyName>` to `ZipDrive.Cli.csproj` so output is `ZipDrive.exe`
- [x] 2.3 Add startup version logging in `Program.cs` — read `AssemblyInformationalVersionAttribute` and log `"ZipDrive {Version} starting"` at Information level before host run
- [x] 2.4 Verify build produces `ZipDrive.exe` (not `ZipDrive.Cli.exe`)
- [x] 2.5 Verify `dotnet build -p:Version=1.0.1-beta` embeds correct version in output assembly

## 3. CI Workflow Update

- [x] 3.1 Update `.github/workflows/ci.yml` — change all `ZipDriveV3` references to `ZipDrive` (solution name, test project paths)
- [x] 3.2 Add `push: branches: [main]` trigger alongside existing `pull_request` trigger
- [x] 3.3 Update the test summary PowerShell `-replace` pattern from `'ZipDriveV3\.'` to `'ZipDrive\.'`

## 4. Release Workflow

- [x] 4.1 Create `.github/workflows/release.yml` — trigger on `push: tags: ['release-*']`
- [x] 4.2 Add steps: checkout, setup .NET 10, restore, build (Release), test (4 test projects), publish single-file win-x64 with `-p:Version` from parsed tag
- [x] 4.3 Add step: create GitHub Release using `gh release create` with `--title "ZipDrive $VERSION"`, attach `ZipDrive.exe` + `appsettings.json`, auto-detect prerelease from hyphen in version, use `--generate-notes`
- [x] 4.4 Add permissions: `contents: write` for release creation

## 5. Documentation Cleanup

- [x] 5.1 Update `README.md` — replace all `ZipDriveV3` with `ZipDrive`, update executable name to `ZipDrive.exe`, update build/run commands, update project structure listing
- [x] 5.2 Update `CLAUDE.md` — full `ZipDriveV3` → `ZipDrive` rename, fix stale "Remaining Work" items (DokanNet adapter, dual-tier cache, etc. are done), update all build commands and project paths, update meter/source name references
- [x] 5.3 Update `src/Docs/CACHING_DESIGN.md` — remove "V3" from title, update any namespace/project references
- [x] 5.4 Update `src/Docs/CONCURRENCY_STRATEGY.md` — remove "V3" from title
- [x] 5.5 Update `src/Docs/STREAMING_ZIP_READER_DESIGN.md` — remove "V3" from title, update namespace references
- [x] 5.6 Update `src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md` — remove "V3" from title, fix `IArchivePrefixTree` status (implemented as `ArchiveTrie`)
- [x] 5.7 Update `src/Docs/VFS_ARCHITECTURE_DESIGN.md` — change status from "Proposed" to "Implemented", replace all `ZipDriveV3` namespace examples
- [x] 5.8 Update `src/Docs/VFS_IMPLEMENTATION_PLAN.md` — change status from "Proposed" to "Implemented", replace all `ZipDriveV3` project references
- [x] 5.9 Update `src/Docs/IMPLEMENTATION_CHECKLIST.md` — replace `ZipDriveV3` references, fix stale "Next Steps" (remove items already done), update test counts

## 6. Final Verification

- [x] 6.1 Full build: `dotnet build ZipDrive.slnx`
- [x] 6.2 Full test: `dotnet test`
- [x] 6.3 Verify no remaining `ZipDriveV3` references in source code (grep across repo, excluding openspec/changes/archive/)
