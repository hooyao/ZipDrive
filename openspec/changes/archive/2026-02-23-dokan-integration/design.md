## Context

ZipDrive V3 has a complete `IVirtualFileSystem` implementation (`ZipVirtualFileSystem`) with archive trie, structure cache, file content cache, and streaming ZIP reader. The VFS layer is fully async, platform-independent, and tested (172 tests). The `ZipDriveV3.Infrastructure.FileSystem` project exists as an empty placeholder. The CLI project has only a "Hello World" Program.cs.

DokanNet 2.3.0.3 provides `IDokanOperations2` - a modernized interface using `ReadOnlyNativeMemory<char>` and `NativeMemory<byte>` for zero-copy interop. The Mirror.cs sample demonstrates the implementation pattern.

### Constraints

- Dokany v2.1.0.1000 is installed on the target machine
- DokanNet 2.3.0.3 targets .NET 8/9 + .NET Standard 2.0 (should work with .NET 10 via netstandard2.0)
- The VFS is async but `IDokanOperations2` is synchronous - requires sync-over-async bridge
- Drive must be explicitly marked read-only at OS level via `DokanOptions.WriteProtection`

## Goals / Non-Goals

**Goals:**

- Mount ZIP archives as a read-only Windows drive letter (e.g., `R:\`)
- Thin DokanNet adapter that delegates everything to `IVirtualFileSystem`
- Standard .NET `BackgroundService` for lifecycle management
- `appsettings.json` configuration with command-line override support
- Clean shutdown on Ctrl+C
- End-to-end smoke test proving files can be read from the mounted drive

**Non-Goals:**

- Write support (read-only only)
- FUSE/Linux support (DokanNet is Windows-only; VFS layer already cross-platform)
- GUI or tray icon
- Windows Service installation (just console app for now)
- Performance optimization beyond what the VFS already provides

## Decisions

### Decision 1: IDokanOperations2 (not IDokanOperations)

**Choice:** Implement the modern `IDokanOperations2` interface.

**Rationale:** Uses `ReadOnlyNativeMemory<char>` and `NativeMemory<byte>` for zero-copy buffer access via `.Span` property. Reduces GC pressure compared to the legacy interface that allocates strings and byte arrays. This is the recommended interface per DokanNet docs.

### Decision 2: Sync-over-async via GetAwaiter().GetResult()

**Choice:** Bridge async VFS calls to synchronous DokanNet methods using `.GetAwaiter().GetResult()`.

**Rationale:** DokanNet uses its own thread pool for dispatch (not the .NET ThreadPool), so blocking won't cause thread pool starvation. This is the simplest correct approach. The Mirror sample uses synchronous I/O directly.

### Decision 3: DokanOptions.WriteProtection for read-only

**Choice:** Mount with `DokanOptions.WriteProtection | DokanOptions.FixedDrive`.

**Rationale:** `WriteProtection` tells the OS driver to reject all write operations at the kernel level before they reach user-mode code. This provides defense-in-depth - even if our adapter has a bug, writes are blocked by the driver. Explorer shows the drive as read-only in properties.

### Decision 4: Host.CreateDefaultBuilder for CLI

**Choice:** Use standard `Host.CreateDefaultBuilder(args)` with `AddHostedService<DokanHostedService>()`.

**Rationale:** Provides `appsettings.json` loading, environment variable binding, command-line argument override (using `:` separator, e.g., `Mount:MountPoint=R:\`), Ctrl+C handling, and DI container out of the box. This is the standard .NET pattern.

### Decision 5: BackgroundService (not IHostedService)

**Choice:** `DokanHostedService` extends `BackgroundService` (not implements `IHostedService` directly).

**Rationale:** `BackgroundService` provides `ExecuteAsync` which runs on a background thread - perfect for the blocking `WaitForFileSystemClosedAsync()` call. `StopAsync` is called automatically on Ctrl+C, where we call `RemoveMountPoint`.

### Decision 6: CreateFile handles read-only semantics

**Choice:** `CreateFile` returns `DokanResult.Success` for existing files/directories and `DokanResult.AccessDenied` for any create/truncate mode.

**Rationale:** Even though `WriteProtection` blocks writes at the driver level, the adapter should still correctly implement `CreateFile` for robustness. Windows calls `CreateFile` for opening existing files too (with `FileMode.Open`), so we must return Success for those.

### Decision 7: No Context object on DokanFileInfo

**Choice:** We don't store anything in `info.Context` since we have no per-file state.

**Rationale:** The VFS handles all state internally (caching, stream management). The adapter is stateless per-call. The Mirror sample stores `FileStream` objects in Context because it needs per-file stream state, but we don't.

### Decision 8: Configuration schema

**Choice:** Two config sections: `Mount` (mount point, archive directory, depth) and `Cache` (reuse existing `CacheOptions`).

```json
{
  "Mount": {
    "MountPoint": "R:\\",
    "ArchiveDirectory": "D:\\Archives",
    "MaxDiscoveryDepth": 6
  },
  "Cache": {
    "MemoryCacheSizeMb": 2048,
    "DiskCacheSizeMb": 10240,
    "SmallFileCutoffMb": 50,
    "DefaultTtlMinutes": 30
  }
}
```

CLI override: `dotnet run -- Mount:MountPoint=Z:\ Cache:MemoryCacheSizeMb=4096`

## Risks / Trade-offs

### [Risk] DokanNet 2.3.0.3 doesn't list .NET 10 as a target
**Mitigation:** It targets .NET Standard 2.0 which is compatible with .NET 10. If compilation issues arise, the FileSystem project can target `net9.0` specifically while the rest of the solution stays on `net10.0`.

### [Risk] Sync-over-async can deadlock in certain contexts
**Mitigation:** DokanNet uses its own thread pool, not SynchronizationContext-based dispatch. No deadlock risk in this scenario. The VFS operations are pure async I/O with no UI thread affinity.

### [Risk] Dokany driver not installed
**Mitigation:** `DokanHostedService` catches `DokanException` on mount failure and logs a clear error message directing the user to install Dokany.

### [Risk] Drive letter already in use
**Mitigation:** Dokan itself reports this error. The hosted service logs the error and shuts down cleanly.

## Open Questions

_(none - design is well-explored from the preceding exploration session)_
