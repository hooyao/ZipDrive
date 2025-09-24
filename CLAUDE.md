# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Prerequisites

- **Windows x64 only** - This project uses DokanNet which is Windows-specific
- **Dokany v2.1.0.1000** must be installed from https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000
- **.NET 8 SDK** with Windows targeting support

## Build and Run Commands

```bash
# Build the entire solution
dotnet build

# Build release configuration
dotnet build -c Release

# Run tests
dotnet test

# Run a specific test
dotnet test --filter "FullyQualifiedName~LruMemoryCacheUnitTest"

# Run the CLI application (must be in the output directory)
cd zip2vd.cli/bin/Debug/net8.0-windows
zip2vd.cli.exe --FilePath <path-to-zip> --MountPath <drive-letter>:\

# Example
zip2vd.cli.exe --FilePath D:\test.zip --MountPath R:\
```

## Architecture Overview

The solution implements a virtual file system that mounts ZIP archives as Windows drives using DokanNet.

### Core Components

1. **zip2vd.core** - Main library implementing the virtual file system
   - `ZipFs` - Implements `IDokanOperations`, handles all file system operations
   - `FileVdService` - Manages the DokanNet instance lifecycle (note: Mount/Unmount not implemented)
   - `LruMemoryCache` - Thread-safe LRU cache with complex locking for concurrent access
   - Dual caching strategy: small files in-memory, large files using memory-mapped temporary files

2. **zip2vd.cli** - Console application host
   - Uses Microsoft.Extensions.Hosting
   - Configured via `appsettings.json` for logging and cache settings
   - `FsHostedService` currently only logs events but doesn't mount the drive

3. **zip2Vd.core.test** - XUnit test project

### Key Architectural Patterns

- **Tree Structure**: ZIP entries are parsed into an in-memory tree (`EntryNode<T>`) for directory navigation
- **Object Pooling**: `ZipArchive` instances are pooled to reduce allocation overhead
- **Caching Strategy**:
  - Files < `SmallFileSizeCutoffInMb` (default 50MB) cached in memory
  - Files ≥ cutoff use memory-mapped files in temp directory
  - LRU eviction when cache size limits reached

### Critical Implementation Details

- The `ZipFs.BuildTree()` method is called on first `FindFiles()` with a global lock
- File reads use the object pool to get `ZipArchive` instances
- Cache uses per-key semaphores for fine-grained locking
- Reflection is used in `ZipArchiveEntryExtensions` to detect platform-specific path separators

### Known Issues

- `IVdService.Mount()` and `Unmount()` throw `NotImplementedException`
- `FsHostedService` doesn't actually start the file system
- Many `IDokanOperations` methods return `DokanResult.NotImplemented`
- No graceful shutdown or error recovery

## Configuration

The application uses `appsettings.json` with these key settings:

```json
{
  "zip": {
    "SmallFileCacheSizeInMb": 2048,      // Total memory cache size
    "LargeFileCacheDir": null,           // Temp file location (null = system temp)
    "SmallFileSizeCutoffInMb": 50,       // Threshold for memory vs disk caching
    "LargeFileCacheSizeInMb": 10240,     // Total disk cache size
    "MaxReadConcurrency": 8               // Max concurrent ZIP reads
  }
}
```

Command line arguments override configuration:
- `--FilePath`: Path to ZIP file to mount
- `--MountPath`: Drive letter to mount (e.g., "R:\\")