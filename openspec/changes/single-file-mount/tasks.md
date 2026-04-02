## 1. Dependencies

- [x] 1.1 Add `Spectre.Console` to `Directory.Packages.props` (central package management)
- [x] 1.2 Add `<PackageReference Include="Spectre.Console" />` to `ZipDrive.Infrastructure.FileSystem.csproj`

## 2. UserNotice Helper

- [x] 2.1 Create `UserNotice` static class in `ZipDrive.Infrastructure.FileSystem` with `Tip()`, `Warning()`, `Error()` methods using Spectre.Console `Panel` widgets
- [x] 2.2 Write unit tests for `UserNotice` (verify panel creation doesn't throw; test with `AnsiConsole.Create(AnsiConsoleSettings)` for testability)

## 3. VFS Single-File Mount

- [x] 3.1 Add `MountSingleFileAsync(string filePath, CancellationToken)` to `IVirtualFileSystem`
- [x] 3.2 Implement `MountSingleFileAsync` in `ArchiveVirtualFileSystem`: call `DescribeFile`, `AddArchiveAsync`, set volume label from filename, set `IsMounted`
- [x] 3.3 Write unit tests for `MountSingleFileAsync` (supported file, unsupported file returns null descriptor indication, volume label derivation, long filename truncation)

## 4. DokanHostedService Validation and Branching

- [x] 4.1 Refactor `ExecuteAsync` validation: replace `Console.Error.WriteLine` error output with `UserNotice.Error()` for empty path and not-found path scenarios
- [x] 4.2 Add three-way detection: `File.Exists` → single-file mode, `Directory.Exists` → directory mode, neither → error
- [x] 4.3 Implement single-file branch: call `MountSingleFileAsync`, handle unsupported format with `UserNotice.Error()` + `WaitForKeyAndStop()`
- [x] 4.4 Add single-file TIP notice after successful single-file mount (before Dokan instance creation)
- [x] 4.5 Skip `StartWatcher()` call in single-file mode
- [x] 4.6 Add empty-directory WARNING notice after `MountAsync` when zero archives discovered

## 5. Integration Tests

- [x] 5.1 Test: drag single ZIP file → mounts successfully with correct virtual path structure (`R:\game.zip\...`)
- [x] 5.2 Test: drag unsupported file → error notice with format list, no mount
- [x] 5.3 Test: drag non-existent path → error notice, no mount
- [x] 5.4 Test: drag empty directory → mounts with warning notice
- [x] 5.5 Test: drag directory with archives → existing behavior unchanged (regression)
- [x] 5.6 Test: single-file mode does not start FileSystemWatcher
