## 1. Rename PrefetchOptions properties

- [x] 1.1 Rename all properties in `src/ZipDrive.Domain/Configuration/PrefetchOptions.cs`: drop `Prefetch` prefix (`PrefetchEnabled` → `Enabled`, etc.), update xmldoc, set `OnRead` default to `true`
- [x] 1.2 Update all property references in `src/ZipDrive.Application/Services/ZipVirtualFileSystem.cs`

## 2. Clean up CacheOptions

- [x] 2.1 Remove unused prefetch properties (lines 142-185) from `src/ZipDrive.Infrastructure.Caching/CacheOptions.cs`

## 3. Config binding and appsettings

- [x] 3.1 Change DI binding in `src/ZipDrive.Cli/Program.cs` from `GetSection("Cache")` to `GetSection("Cache:Prefetch")`
- [x] 3.2 Restructure `src/ZipDrive.Cli/appsettings.jsonc`: nest prefetch keys under `"Prefetch": { ... }` inside `"Cache"`, drop `Prefetch` prefix from key names, set `"OnRead": true`

## 4. Update tests

- [x] 4.1 Update `tests/ZipDrive.Domain.Tests/PrefetchIntegrationTests.cs` property names
- [x] 4.2 Update `tests/ZipDrive.Domain.Tests/ZipVirtualFileSystemTests.cs` property names
- [x] 4.3 Update `tests/ZipDrive.TestHelpers/VfsTestFixture.cs` property names
- [x] 4.4 Update `tests/ZipDrive.EnduranceTests/EnduranceTest.cs` property names

## 5. Update documentation

- [x] 5.1 Update `README.md` config key paths and defaults table
- [x] 5.2 Add comprehensive "How Prefetch Works" section to `README.md`: explain the mechanism (span selection, coalescing read, sibling warming), side effects (directory-enumerating software triggering massive I/O, cache overflow), and how to troubleshoot (disable `OnRead`, `OnListDirectory`, or `Enabled` entirely if abnormal I/O is observed)
- [x] 5.3 Update `CLAUDE.md` config example and references

## 6. Verify

- [x] 6.1 Build solution (`dotnet build ZipDrive.slnx`)
- [x] 6.2 Run all tests (`dotnet test`)
