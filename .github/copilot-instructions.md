# Copilot Custom Instructions

## Project Overview

ZipDrive is a clean-architecture Windows application that mounts ZIP archives as virtual drives using DokanNet. It targets .NET 10.0 / C# 13 and runs on Windows x64 only.

## Architecture

The solution follows Clean Architecture (Onion Architecture) with strict dependency rules:

- **Domain** (`src/ZipDrive.Domain`): Interfaces, models, exceptions. Zero external dependencies.
- **Application** (`src/ZipDrive.Application`): Path resolution, archive discovery, VFS orchestration.
- **Infrastructure**: Caching (`Infrastructure.Caching`), ZIP reader (`Infrastructure.Archives.Zip`), DokanNet adapter (`Infrastructure.FileSystem`).
- **Presentation** (`src/ZipDrive.Cli`): Entry point, DI, OpenTelemetry wiring.

Dependencies flow inward: Presentation -> Application -> Domain <- Infrastructure. Infrastructure implements Domain interfaces but never depends on Application or Presentation.

## Package Management

All NuGet package versions are centrally managed in `Directory.Packages.props` at the repo root. Individual `.csproj` files must use `<PackageReference Include="..." />` without a `Version` attribute. Flag any PR that adds `Version=` to a `<PackageReference>` in a `.csproj` file.

## Code Review Guidelines

### Concurrency Rules (Critical)

The caching subsystem (`Infrastructure.Caching`) is the most sensitive component. When reviewing changes to it, verify:

1. Cache hits must be lock-free (`ConcurrentDictionary.TryGetValue` only — no global locks).
2. Materialization uses per-key `Lazy<Task<T>>` with `ExecutionAndPublication` mode to prevent thundering herd.
3. Different cache keys must never block each other during materialization.
4. Eviction must not block reads — eviction uses a separate lock.
5. TTL logic must use `TimeProvider`, never `DateTime.UtcNow` or `DateTimeOffset.UtcNow` directly. This enables deterministic testing with `FakeTimeProvider`.
6. `ICacheHandle<T>` must always be disposed (borrow/return pattern). Look for missing `using` or `await using` on handles.
7. Entries with `RefCount > 0` must never be evicted.

### Code Style

- Nullable reference types are enabled everywhere (`<Nullable>enable</Nullable>`). Flag any suppression (`!`) that isn't clearly justified.
- All I/O must use `async`/`await`. Flag synchronous file or network access.
- Domain models should be immutable `record` types.
- Services must be registered via DI — flag direct `new` instantiation of service classes.
- Logging must use `ILogger<T>` with structured logging (named placeholders, not string interpolation).
- All async methods must accept and forward `CancellationToken`.

### Security

- No secrets or credentials in committed code (`appsettings.jsonc` values should use placeholders or defaults only).
- File paths from external input must be validated and normalized to prevent path traversal.

### Testing

- New code requires unit or integration tests.
- Tests use xUnit and FluentAssertions.
- TTL/time-dependent tests must use `FakeTimeProvider`, never real delays.
- Concurrency tests should verify: thundering herd prevention (same key), parallel materialization (different keys), and eviction under load.

### Build and Validation

```bash
dotnet build ZipDrive.slnx
dotnet test
```

All tests must pass. The CI workflow runs `build-and-test` on `windows-latest` with .NET 10.

### Performance Considerations

Flag changes that may violate these targets:
- Cache hit overhead: < 1ms
- Eviction latency: < 1ms (mark phase only, cleanup is async)
- Path resolution: < 1ms
- Structure cache build: < 100ms for 10,000-entry ZIP
