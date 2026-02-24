## Why

ZipDrive V3 has a complete platform-independent VFS layer (`IVirtualFileSystem`, archive trie, structure cache, file content cache, ZIP reader) with 172 passing tests. However, none of this is accessible to end users because there is no DokanNet adapter or CLI application. This change connects the VFS to a real Windows virtual drive via DokanNet and wraps it in a standard .NET `BackgroundService` with `appsettings.json` configuration.

## What Changes

- **New `DokanFileSystemAdapter : IDokanOperations2`**: Thin adapter (~250 lines) that translates DokanNet native memory types and synchronous calls to `IVirtualFileSystem` async operations. Mounts as `WriteProtection` (OS-level read-only drive).
- **New `DokanHostedService : BackgroundService`**: Manages Dokan instance lifecycle - mounts VFS on start, unmounts on Ctrl+C / stop.
- **Revised CLI `Program.cs`**: Standard `Host.CreateDefaultBuilder(args)` with DI wiring for all services. Supports `appsettings.json` and command-line overrides (e.g., `Mount:MountPoint=R:\`).
- **New `appsettings.json`**: Configuration for mount point, archive directory, discovery depth, and cache settings.
- **New `MountOptions` class**: Strongly-typed options bound from `Mount` config section.
- **End-to-end smoke test**: Generate test ZIPs, mount drive, read files from mounted path, verify SHA-256 integrity for small and large files.

## Capabilities

### New Capabilities

- `dokan-adapter`: DokanNet `IDokanOperations2` implementation that delegates to `IVirtualFileSystem`. Handles `ReadOnlyNativeMemory<char>` → string conversion, `NativeMemory<byte>` buffer writes, `FindFileInformation`/`ByHandleFileInformation` construction, and sync-over-async bridging. Mounts with `DokanOptions.WriteProtection | DokanOptions.FixedDrive`.
- `dokan-hosted-service`: .NET `BackgroundService` managing DokanNet instance lifecycle. Uses `DokanInstanceBuilder` to configure and mount, `WaitForFileSystemClosedAsync` to block, and `RemoveMountPoint` on shutdown.
- `cli-application`: Console application entry point with `Host.CreateDefaultBuilder`, DI service registration, `appsettings.json` + command-line configuration binding, and Serilog structured logging.

### Modified Capabilities

_(none - existing VFS layer is consumed as-is)_

## Impact

- **Modified projects**: `ZipDriveV3.Infrastructure.FileSystem` (add DokanNet package + adapter), `ZipDriveV3.Cli` (full rewrite of Program.cs + appsettings.json)
- **New NuGet dependencies**: `DokanNet` (2.3.0.3), `Microsoft.Extensions.Hosting`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`
- **Runtime requirement**: Dokany v2.1.0.1000 must be installed on the machine
- **Platform**: Windows-only (DokanNet is Windows-specific; the VFS layer itself remains cross-platform)
