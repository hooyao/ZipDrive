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

---

## Project Memory: Deep Understanding for V3 Rewrite

> **Last Updated**: 2025-11-23
> This section documents comprehensive understanding of the ZipDrive project for the v3 rewrite initiative.

### 1. Project Purpose & Vision

**What ZipDrive Does:**
ZipDrive (zip2vd - "ZIP to Virtual Drive") is a Windows-only .NET application that mounts ZIP archive files as virtual Windows drives using DokanNet (a user-mode file system driver). This allows users to browse and read files inside ZIP archives as if they were regular folders in Windows Explorer, without extracting them to disk.

**Problem Being Solved:**
- Traditional workflow: Extract → Use → Delete (wastes storage and time)
- ZipDrive workflow: Mount → Use directly → Unmount

**Intended User Experience:**
```bash
# User runs command
zip2vd.cli.exe --FilePath D:\archive.zip --MountPath R:\

# ZIP appears as R:\ in Windows Explorer
# User can browse folders, open files, search contents
# Files are cached automatically for performance
# No disk space consumed (except cache)
```

**Current State: INCOMPLETE**
- Core file operations work (read, list, get metadata)
- **Critical bugs**: Mount/Unmount not implemented, hosted service doesn't start drive
- Result: Application compiles but doesn't actually mount drives

### 2. Architecture Analysis

#### 2.1 Current V1 Architecture (Production)

**Solution Structure:**
```
zip2vd.sln
├── zip2vd.core/          [Core library - 1200+ LOC]
├── zip2vd.cli/           [CLI host - 100 LOC]
└── zip2Vd.core.test/     [Tests - minimal coverage]
```

**Component Responsibilities:**

**A. ZipFs.cs (524 lines) - MONOLITHIC**
- Location: `zip2vd.core/ZipFs.cs`
- Implements: `DokanNet.IDokanOperations` (25+ methods)
- Responsibilities (TOO MANY):
  - DokanNet callback handling
  - ZIP archive lifecycle (object pooling)
  - Tree building and navigation
  - Dual-tier caching (small + large files)
  - File reading with offset support
  - Volume information

**Critical Methods:**
- `CreateFile()` (lines 75-96): Always returns Success, minimal validation
- `ReadFile()` (lines 98-243): Main read logic with caching
- `FindFiles()` (lines 273-314): Lazy tree building + directory listing
- `GetFileInformation()` (lines 256-271): File metadata retrieval
- `BuildTree()` (lines 422-489): ZIP entry tree construction
- `LocateNode()` (lines 491-517): Path-based tree navigation

**Violation**: Single Responsibility Principle - this class does everything!

**B. LruMemoryCache<TKey, TValue> (180 lines) - COMPLEX**
- Location: `zip2vd.core/Cache/LruMemoryCache.cs`
- Hand-rolled LRU with size-based eviction
- **Concurrency Strategy**:
  - Global `_cacheLock` for structure mutations
  - Per-key `Semaphore` for value access
  - RAII pattern via `CacheItem.Dispose()` releases semaphore
- Data structures:
  - `LinkedList<CacheItem>` for LRU ordering
  - `ConcurrentDictionary<TKey, LinkedListNode>` for O(1) lookup
  - `ConcurrentDictionary<TKey, Semaphore>` for key locks

**Issues**:
- Overly complex for use case
- Holding global lock while acquiring key locks (deadlock risk)
- Lazy evaluation inside lock scope
- Should use `Microsoft.Extensions.Caching.Memory.MemoryCache` instead

**C. FileVdService (59 lines) - INCOMPLETE**
- Location: `zip2vd.core/FileVdService.cs`
- Wrapper around `DokanNet.DokanInstance`
- **Problem**: Mount/Unmount throw `NotImplementedException`
```csharp
public void Mount() {
    throw new NotImplementedException();
}
```
- Should call: `_dokanInstance.WaitForFileSystemClosed()`

**D. FsHostedService (47 lines) - BROKEN**
- Location: `zip2vd.cli/FsHostedService.cs`
- Implements `IHostedService`
- **Problem**: `StartAsync()` just logs and returns
```csharp
public Task StartAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Starting");
    return Task.CompletedTask;  // DOES NOTHING!
}
```
- Should call: `_vdService.Mount()` in background task

**E. EntryNode<TAttr> (37 lines) - SIMPLE**
- Location: `zip2vd.core/Common/EntryNode.cs`
- Tree node representing directory hierarchy
- Fields:
  - `TAttr? Attributes`: ZIP entry metadata
  - `bool IsDirectory`
  - `string Name`
  - `Dictionary<string, EntryNode<TAttr>> _childNodes`
  - `EntryNode<TAttr>? Parent`
  - `FileInformation FileInformation`: DokanNet struct

#### 2.2 Data Flow Analysis

**Startup Flow (Current - Broken):**
```
Program.cs
  ↓ Create host builder
  ↓ Load appsettings.json + command line args
  ↓ Configure Serilog
  ↓ Register DI services:
    - FileVdOptions (FilePath, MountPath)
    - ArchiveFileSystemOptions (cache config)
    - FileVdService (singleton)
    - FsHostedService
  ↓ Build and RunAsync()
  ↓ FsHostedService.StartAsync() called
  ↓ ❌ JUST LOGS "Starting" - DRIVE NEVER MOUNTS
  ↓ App waits indefinitely...
```

**File Read Flow (When Working):**
```
1. Windows Explorer: Open R:\folder\file.txt
   ↓
2. Dokan → ZipFs.ReadFile("\\folder\\file.txt", buffer, offset)
   ↓
3. Split path: ["folder", "file.txt"]
   ↓
4. LocateNode(parts) → Navigate tree → Find EntryNode
   ↓
5. Get node.Attributes.FullPath (ZIP internal path)
   ↓
6. Get ZipArchive from pool (_zipArchivePool.Get())
   ↓
7. archive.GetEntry(fullPath) → ZipArchiveEntry
   ↓
8. Check file size:

   IF size < 50MB (default SmallFileSizeCutoffInMb):
   ├─ Check _smallFileCache (in-memory byte[])
   ├─ Cache miss? Read entire file into memory
   ├─ Copy byte[offset : offset+bufferSize] to output
   └─ Return cached byte[]

   ELSE (large file):
   ├─ Check _largeFileCache (memory-mapped files)
   ├─ Cache miss?
   │  ├─ Create temp file: Path.GetTempPath() + {guid}.zip2vd
   │  ├─ MemoryMappedFile.CreateFromFile(tempFile, size)
   │  ├─ Copy ZIP entry to memory-mapped file
   │  └─ Return LargeFileCacheEntry wrapper
   ├─ mmfAccessor.ReadArray<byte>(offset, buffer)
   └─ Return buffer
   ↓
9. Return ZipArchive to pool
   ↓
10. Return DokanResult.Success to Dokan
    ↓
11. Windows delivers bytes to application
```

**Tree Building Flow (Lazy - First Directory Listing):**
```
1. FindFiles("\\") called (list root directory)
   ↓
2. lock (_zipFileLock)  ← ⚠️ GLOBAL LOCK - BLOCKS ALL THREADS
   ↓
3. if (!_buildTree):
   ├─ archive = _zipArchivePool.Get()
   ├─ BuildTree(_root, archive):
   │  ├─ foreach (entry in archive.Entries):
   │  │  ├─ ParsePath(entry) → ["dir", "subdir", "file.txt"]
   │  │  ├─ currentNode = root
   │  │  ├─ foreach (part in path):
   │  │  │  ├─ if part not in currentNode.children:
   │  │  │  │  └─ Create new EntryNode (dir or file)
   │  │  │  └─ currentNode = currentNode.children[part]
   │  │  └─ Set leaf node attributes (size, dates, etc.)
   │  └─ return root
   ├─ _zipArchivePool.Return(archive)
   └─ _buildTree = true
   ↓
4. unlock (_zipFileLock)
   ↓
5. LocateNode(split path) → Find directory node
   ↓
6. Return node.ChildNodes as List<FileInformation>
```

**Performance Issue**: Tree building holds global lock, blocking ALL directory operations during scan. For large ZIPs (thousands of entries), this can take seconds.

#### 2.3 Key Algorithms

**Algorithm 1: LRU Cache Eviction**
- Location: `LruMemoryCache.cs:34-97`
- Type: Size-based LRU (not count-based)
- Complexity: O(1) for hit, O(evicted_count) for miss with eviction
- Pseudocode:
```
BorrowOrAdd(key, factory, size):
  lock (global):
    keyLock = getSemaphore(key)
    keyLock.acquire()

    if key in cache:
      move to end of LRU list  // Most recent
      return item

    while currentSize + size >= limit:
      victim = lruList.first   // Least recent
      victimLock = getSemaphore(victim.key)
      victimLock.acquire()
      remove victim from cache
      dispose victim.value
      victimLock.release()
      currentSize -= victim.size

    item = new CacheItem(key, Lazy(factory), size, keyLock)
    add to end of LRU list
    currentSize += size
    return item  // keyLock released on Dispose()
```

**Algorithm 2: Path Parsing (Platform-Aware)**
- Location: `ZipArchiveEntryExtensions.cs:16-44`
- **Uses Reflection** (code smell!):
```csharp
FieldInfo? fieldInfo = entry.GetType()
    .GetField("_versionMadeByPlatform",
              BindingFlags.NonPublic | BindingFlags.Instance);

byte platform = (byte)fieldInfo.GetValue(entry);

if (platform == 3) {  // Unix
    return entry.FullName.Split('/');
} else {              // Windows
    return entry.FullName.Split('\\', '/', ':');
}
```
- **Issue**: Accesses private BCL field, fragile across .NET versions
- **Better**: ZIP spec mandates '/', always use that

**Algorithm 3: Object Pool Policy**
- Location: `ZipArchivePooledObjectPolicy.cs`
- Pattern: Simple pooling, no validation on return
```csharp
public ZipArchive Create() {
    return new ZipArchive(
        File.OpenRead(_filePath),
        ZipArchiveMode.Read,
        leaveOpen: false,
        Encoding.GetEncoding(936)  // ANSI Chinese?
    );
}

public bool Return(ZipArchive obj) {
    return true;  // Always accept back
}
```
- **Issue**: No health check, corrupted archive stays in pool

#### 2.4 Configuration Schema

**FileVdOptions** (Command Line):
```csharp
public string FilePath { get; set; } = "";   // ZIP path
public string MountPath { get; set; } = "";  // Drive letter
```

**ArchiveFileSystemOptions** (appsettings.json → "zip"):
```csharp
public int SmallFileCacheSizeInMb { get; set; } = 1024;      // Memory cache
public string? LargeFileCacheDir { get; set; } = null;       // Temp dir
public int SmallFileSizeCutoffInMb { get; set; } = 100;      // Threshold
public int LargeFileCacheSizeInMb { get; set; } = 10240;     // Disk cache
public int MaxReadConcurrency { get; set; } = 4;             // Pool size
```

**Example Production Config:**
```json
{
  "zip": {
    "SmallFileCacheSizeInMb": 2048,        // 2GB RAM
    "LargeFileCacheDir": null,             // System temp
    "SmallFileSizeCutoffInMb": 50,         // 50MB cutoff
    "LargeFileCacheSizeInMb": 10240,       // 10GB disk
    "MaxReadConcurrency": 8                // 8 ZipArchive instances
  }
}
```

### 3. Design Patterns Used

| Pattern | Location | Purpose | Quality |
|---------|----------|---------|---------|
| **Object Pooling** | `ZipArchivePooledObjectPolicy.cs` | Reuse ZipArchive instances | ✅ Good |
| **Dual-Tier Caching** | `ZipFs.cs:47-52` | Memory vs disk trade-off | ⚠️ Works but complex |
| **Lazy Initialization** | `ZipFs.cs:275-280` | Defer tree building | ⚠️ Causes first-call latency |
| **Tree Structure** | `EntryNode.cs` | Hierarchical navigation | ✅ Good |
| **Adapter Pattern** | `DokanLogger.cs` | ILogger → DokanNet.ILogger | ✅ Good |
| **Options Pattern** | Throughout | Configuration binding | ✅ Good |
| **Dependency Injection** | `Program.cs` | Service lifetime management | ✅ Good |
| **RAII (Dispose)** | `LruMemoryCache.cs` | Automatic lock release | ⚠️ Clever but fragile |

### 4. Critical Issues & Technical Debt

#### 4.1 Blocking Issues (Prevents Use)

**A. Mount/Unmount Not Implemented**
- File: `zip2vd.core/FileVdService.cs:39-47`
- Both methods throw `NotImplementedException`
- Impact: **Cannot start file system**

**B. Hosted Service Does Nothing**
- File: `zip2vd.cli/FsHostedService.cs:36-40`
- `StartAsync()` just logs, never calls `Mount()`
- Impact: **Drive never appears in Windows**

#### 4.2 Architectural Debt

**A. Monolithic ZipFs (524 lines)**
- Violates Single Responsibility Principle
- Combines: DokanNet adapter + caching + tree + ZIP access
- Hard to test, modify, or extend

**Should Split Into:**
1. `DokanFileSystemAdapter`: DokanNet interface only
2. `ArchiveTreeManager`: Tree building/navigation
3. `FileContentProvider`: Read operations
4. `CacheManager`: Unified caching
5. `ArchivePool`: ZIP lifecycle

**B. Hand-Rolled LRU Cache**
- 180 lines of complex locking logic
- Reinvents `Microsoft.Extensions.Caching.Memory.MemoryCache`
- Per-key semaphores + global lock = deadlock risk
- No observability (miss rate, eviction count, etc.)

**Should Replace With**: Built-in `MemoryCache` with eviction callbacks

**C. Synchronous I/O Throughout**
- No async/await despite heavy I/O
- Blocks thread pool threads
- Poor scalability under concurrent load

**Should Convert To**: Async APIs (`IDokanOperations` supports it)

**D. Global Lock Bottleneck**
- Tree building holds `_zipFileLock` (line 275)
- Blocks ALL `FindFiles()` calls
- No progress reporting for long operations

**Should Use**: Fine-grained locks or lock-free structures

#### 4.3 Code Quality Issues

**A. Reflection for Path Parsing**
- File: `ZipArchiveEntryExtensions.cs:18-25`
- Accesses private `_versionMadeByPlatform` field
- Breaks if BCL implementation changes
- Fix: Always use '/' (ZIP spec)

**B. Magic Numbers**
```csharp
_smallFileSizeCutOff = options.SmallFileSizeCutoffInMb * 1024L * 1024L;
```
- Conversion to bytes scattered
- Should use: `const long BytesPerMb = 1024 * 1024;`

**C. Inconsistent Error Handling**
```csharp
catch (FileNotFoundException) {
    return DokanResult.InternalError;  // Should be FileNotFound!
}
```

**D. Commented Code** (Lines 150-169)
- Dead RecyclableMemoryStream usage
- Should delete or use it

**E. Unused Dependencies**
- `Microsoft.IO.RecyclableMemoryStream`: Included but never used
- `Moq`: Test dependency, no mocks written

#### 4.4 Missing Features

- ❌ No password-protected ZIP support
- ❌ No other archive formats (TAR, RAR, 7Z)
- ❌ No write support (read-only only)
- ❌ No progress reporting for operations
- ❌ No health metrics or observability
- ❌ No graceful shutdown (abrupt DokanNet stop)
- ❌ No ZIP change detection (stale cache)
- ❌ No multi-ZIP mounting
- ❌ No symbolic link support
- ❌ No alternate data streams

#### 4.5 Testing Gaps

**Current Coverage**: ~5%
- Only `LruMemoryCacheUnitTest.cs` exists
- Tests only cache, not core functionality

**Missing Tests:**
- ❌ ZipFs operations (95% of code!)
- ❌ Tree building edge cases
- ❌ Concurrent read scenarios
- ❌ Cache eviction under memory pressure
- ❌ Large file handling
- ❌ Error cases (corrupt ZIP, missing files)
- ❌ DokanNet integration tests

### 5. V2 Architecture Analysis

#### 5.1 V2 Vision (From MULTI_ARCHIVE_ARCHITECTURE.md)

**Location**: `/root/ZipDrive/v2/`

**Key Improvement**: Multi-Archive Support
- Mount entire **folder of ZIPs**, not just one
- Each archive appears as top-level directory
- Central registry tracks all archives
- File system watcher detects additions/removals

**Example**:
```
MountPath: R:\
ArchiveFolder: D:\archives\

R:\
├── archive1.zip\
│   ├── file1.txt
│   └── folder\
├── archive2.zip\
│   └── file2.txt
└── data.7z\
    └── file3.txt
```

#### 5.2 V2 Component Structure

```
v2/
├── ZipDriveV2.Core/              # Domain abstractions
│   └── Abstractions.cs           # All interfaces
├── ZipDriveV2.Archives/          # Format providers
│   └── StubZipArchiveProvider.cs # TODO: Real impl
├── ZipDriveV2.FileSystem/        # DokanNet adapter
│   └── DokanAdapter.cs           # TODO: Real impl
├── ZipDriveV2.Caching/           # Caching layer
│   └── Interfaces.cs             # TODO: Define
├── ZipDriveV2.Cli/               # CLI host
│   └── Program.cs                # TODO: Wire up
└── ZipDriveV2.Tests/             # Tests
    └── StubProviderTests.cs      # Placeholder
```

#### 5.3 V2 Key Abstractions

**IArchiveProvider** (Pluggable Formats):
```csharp
public interface IArchiveProvider {
    string FormatId { get; }  // "zip", "tar", "7z"
    bool CanOpen(string fileName, ReadOnlySpan<byte> headerSample);
    IArchiveSession Open(ArchiveOpenContext context);
}
```

**IArchiveSession** (Per-Archive Instance):
```csharp
public interface IArchiveSession : IAsyncDisposable {
    ArchiveInfo Info { get; }
    IEnumerable<IArchiveEntry> Entries();
    IArchiveEntry? GetEntry(string path);
    Stream OpenRead(IArchiveEntry entry, Range? range, CancellationToken ct);
    IArchiveCapabilities Capabilities { get; }
}
```

**IArchiveRegistry** (Multi-Archive Management):
```csharp
public interface IArchiveRegistry {
    IReadOnlyCollection<ArchiveDescriptor> List();
    ArchiveDescriptor? Get(string archiveKey);
    event EventHandler<ArchivesChangedEventArgs>? Changed;
}
```

**IPathResolver** (Virtual Path Mapping):
```csharp
public interface IPathResolver {
    (string? ArchiveKey, string InnerPath, PathResolutionStatus Status)
        Split(string rawPath);
}
```

**Example Path Resolution**:
```
Input:  "\\archive1.zip\\folder\\file.txt"
Output: (ArchiveKey="archive1.zip", InnerPath="folder/file.txt", Status=Ok)
```

#### 5.4 V2 Status: INCOMPLETE

**What Exists**:
- ✅ Clean interface definitions
- ✅ Stub implementations (no logic)
- ✅ Project structure
- ✅ Basic test scaffolding

**What's Missing**:
- ❌ Real `ZipArchiveProvider` implementation
- ❌ DokanNet integration
- ❌ Tree management
- ❌ Caching layer
- ❌ Registry implementation
- ❌ CLI wiring
- ❌ All tests

**Critical Bug**: V2 targets .NET 10 (unreleased!)
```json
// v2/global.json
{
  "sdk": {
    "version": "10.0.100",  // ❌ Doesn't exist!
    "rollForward": "disable"
  }
}
```
Should be: `8.0.100` (like v1)

### 6. V3 Rewrite Recommendations

#### 6.1 Core Principles

1. **SOLID Principles**
   - Single Responsibility: One class, one job
   - Open/Closed: Extensible via interfaces
   - Liskov Substitution: Proper abstractions
   - Interface Segregation: Focused contracts
   - Dependency Inversion: Depend on abstractions

2. **Clean Architecture**
   - Domain layer (no external dependencies)
   - Application layer (use cases)
   - Infrastructure layer (DokanNet, System.IO.Compression)
   - Presentation layer (CLI)

3. **Modern .NET Patterns**
   - Async/await throughout
   - `IAsyncDisposable` for resources
   - `IOptions<T>` for configuration
   - `ILogger<T>` for logging
   - `IHostedService` for lifecycle
   - Built-in DI container

4. **Testability**
   - Interfaces for everything external
   - No static dependencies
   - Pure functions where possible
   - Integration test support

5. **Observability**
   - Structured logging (Serilog)
   - Metrics (counters, gauges)
   - Health checks
   - Distributed tracing ready

#### 6.2 Proposed V3 Component Breakdown

**Domain Layer** (`ZipDriveV3.Domain`):
```
- IArchiveProvider              // Format abstraction
- IArchiveSession               // Per-archive ops
- IArchiveEntry                 // File/directory metadata
- IFileSystemTree               // Tree structure
- IPathResolver                 // Path parsing
- Domain models (immutable records)
```

**Application Layer** (`ZipDriveV3.Application`):
```
- IArchiveRegistry              // Multi-archive management
- IFileCacheService             // Caching facade
- IFileSystemService            // High-level FS ops
- Use cases:
  - MountArchiveUseCase
  - ReadFileUseCase
  - ListDirectoryUseCase
```

**Infrastructure Layer**:
- `ZipDriveV3.Archives.Zip`     // ZIP implementation
- `ZipDriveV3.Archives.Tar`     // TAR implementation (future)
- `ZipDriveV3.Caching`          // Cache implementations
- `ZipDriveV3.FileSystem.Dokan` // DokanNet adapter

**Presentation Layer**:
- `ZipDriveV3.Cli`              // Console app
- `ZipDriveV3.Service`          // Windows Service (future)

#### 6.3 Key Improvements Over V1/V2

| Aspect | V1 | V2 | V3 (Proposed) |
|--------|----|----|---------------|
| **Architecture** | Monolithic | Layered | Clean Architecture |
| **Async** | No | Planned | Yes (throughout) |
| **Caching** | Custom LRU | Not impl | Built-in MemoryCache |
| **Multi-Archive** | No | Yes | Yes (enhanced) |
| **Formats** | ZIP only | Pluggable | ZIP + extensible |
| **Concurrency** | Global locks | Unknown | Fine-grained async |
| **Testing** | 5% coverage | 0% | 80%+ target |
| **Observability** | Logs only | Unknown | Logs + metrics + health |
| **Error Handling** | Inconsistent | Unknown | Comprehensive |
| **Documentation** | Minimal | None | Full (XML + markdown) |

#### 6.4 Technology Stack for V3

**Core Framework**:
- .NET 8.0 (LTS, mature)
- C# 12 (latest stable)

**File System**:
- DokanNet 2.1.0 (proven)
- Consider: Evaluate DokanNet 3.0 when stable

**Archive Handling**:
- System.IO.Compression (ZIP)
- SharpCompress (TAR, RAR, 7Z)

**Caching**:
- Microsoft.Extensions.Caching.Memory (in-memory)
- Memory-mapped files (large files)
- Consider: Redis for distributed scenarios

**Async I/O**:
- System.IO.Pipelines (high-performance)
- System.Threading.Channels (producer-consumer)

**Configuration**:
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.Options

**Logging**:
- Serilog (structured logging)
- Serilog.Sinks.* (Console, File, Seq)

**Dependency Injection**:
- Microsoft.Extensions.DependencyInjection

**Testing**:
- XUnit (test framework)
- FluentAssertions (readable assertions)
- NSubstitute (mocking)
- Testcontainers (integration tests)

**Code Quality**:
- Nullable reference types enabled
- Roslyn analyzers
- EditorConfig for style

#### 6.5 Critical Path to V3 MVP

**Phase 1: Foundation** (Week 1)
1. Fix v2 `global.json` (.NET 10 → .NET 8)
2. Define all domain interfaces
3. Set up project structure
4. Configure CI/CD pipeline
5. Enable nullable reference types

**Phase 2: Core Implementation** (Week 2-3)
6. Implement `ZipArchiveProvider` (real, not stub)
7. Implement `FileSystemTree` (path resolution, navigation)
8. Implement `CacheService` (using MemoryCache)
9. Implement `ArchiveRegistry` (multi-archive support)
10. Add comprehensive unit tests (80%+ coverage)

**Phase 3: DokanNet Integration** (Week 4)
11. Implement `DokanFileSystemAdapter`
12. Wire up all IDokanOperations methods
13. Add integration tests with test ZIPs
14. Test mount/unmount lifecycle

**Phase 4: CLI & Polish** (Week 5)
15. Implement CLI with proper argument parsing
16. Add hosted service (working this time!)
17. Add health checks and metrics
18. Write user documentation
19. Performance testing and optimization

**Phase 5: Release** (Week 6)
20. Beta testing with real-world ZIPs
21. Fix critical bugs
22. Write migration guide (v1 → v3)
23. Publish v3.0.0 release

#### 6.6 Non-Functional Requirements

**Performance Targets**:
- Mount 10GB ZIP in < 5 seconds (lazy tree build)
- Read 1MB file in < 50ms (cached)
- List 1000-file directory in < 100ms
- Support 10+ concurrent file reads

**Resource Limits**:
- Memory cache: 2GB default (configurable)
- Disk cache: 10GB default (configurable)
- Max open archives: 16 (pool size)

**Reliability**:
- Graceful handling of corrupt ZIPs
- Automatic retry on transient failures
- Clean unmount even on crash (DokanNet cleanup)

**Security**:
- No arbitrary code execution (ZIP bomb protection)
- Path traversal prevention
- Resource limits (decompression bomb detection)

### 7. Migration Strategy (V1 → V3)

**Backward Compatibility**:
- Keep v1 configuration format (appsettings.json "zip" section)
- Support same command-line arguments
- Maintain drive mounting behavior

**Breaking Changes**:
- Drop v1 custom LRU cache (invisible to users)
- Drop reflection-based path parsing (internal)
- May change log format (Serilog config change)

**Migration Guide**:
```markdown
# V1 → V3 Migration

## No Code Changes Required
V3 maintains CLI compatibility.

## Configuration Changes
- `MaxReadConcurrency` renamed to `MaxConcurrentArchives`
- New optional settings:
  - `TreeCacheSizeInMb`: Tree structure cache
  - `EnableMetrics`: Prometheus endpoint

## New Features
- Multi-archive mounting (mount folder of ZIPs)
- TAR/7Z support (via providers)
- Health endpoint: `http://localhost:5000/health`
```

### 8. Lessons Learned from V1

**What Worked Well**:
1. ✅ Object pooling (ZipArchive reuse)
2. ✅ Dual caching strategy (memory + disk)
3. ✅ Tree structure (efficient navigation)
4. ✅ Options pattern (configuration)
5. ✅ DokanNet integration (file system works)

**What Didn't Work**:
1. ❌ Monolithic design (ZipFs does everything)
2. ❌ Hand-rolled caching (complex, buggy)
3. ❌ Synchronous I/O (poor scalability)
4. ❌ Global locks (serializes operations)
5. ❌ Lazy tree building (first-call penalty)
6. ❌ No tests (can't refactor safely)
7. ❌ Reflection hacks (fragile, breaks)
8. ❌ Incomplete implementation (Mount not working)

**Key Insight**: Good ideas, poor execution. V3 should take proven patterns (pooling, caching, tree) and implement them properly with modern best practices.

### 9. Success Metrics for V3

**Code Quality**:
- [ ] Test coverage > 80%
- [ ] Zero reflection usage
- [ ] Zero global locks
- [ ] All async operations
- [ ] No TODO/HACK comments in main branch

**Functionality**:
- [ ] Mount/unmount works reliably
- [ ] Multi-archive support functional
- [ ] ZIP, TAR, 7Z formats supported
- [ ] Graceful error handling
- [ ] Health checks passing

**Performance**:
- [ ] 10GB ZIP mounts in < 5s
- [ ] 1MB cached read < 50ms
- [ ] Passes stress test (100 concurrent reads)
- [ ] No memory leaks (24hr soak test)

**Documentation**:
- [ ] README with quick start
- [ ] Architecture decision records (ADRs)
- [ ] API documentation (XML comments)
- [ ] Configuration guide
- [ ] Troubleshooting guide

---

## Summary: Ready for V3 Rewrite

This comprehensive memory documents everything needed to rewrite ZipDrive from scratch. The v1 implementation proves the concept works but suffers from architectural debt. The v2 attempt shows good design thinking but was never completed. V3 should combine v1's proven patterns with v2's clean architecture, implemented with modern .NET practices and full test coverage.

**Next Steps**: Begin V3 implementation following the critical path outlined above.