# ZipDrive - Chunked Extraction Design Document

**Version:** 1.0
**Date:** 2026-02-27
**Status:** Proposed
**Author:** Claude Code

---

## Executive Summary

**The Problem**: For files routed to the disk tier (>= 50MB), the current `DiskStorageStrategy` extracts the *entire* decompressed file to a temp file before serving the first byte. A 5GB video at ~200MB/s decompression throughput blocks the user for **25 seconds** before any data is available. Even a 200MB file takes ~1 second — noticeable lag for every disk-tier cache miss.

**The Solution**: `ChunkedDiskStorageStrategy` **replaces** `DiskStorageStrategy` as the disk tier storage strategy. It decompresses files incrementally in fixed-size chunks (default 10MB). Completed chunks are immediately available for random-access reads while extraction continues in the background. The returned `ChunkedStream` is transparent to callers — it implements `Stream` and blocks only when a reader requests data from a chunk that hasn't been extracted yet.

**Integration Approach**: Drop-in replacement storage strategy within the existing `GenericCache<Stream>` architecture. Zero changes to `GenericCache`, `ICache<T>`, `ICacheHandle<T>`, `CacheEntry`, or the borrow/return reference counting mechanism. `FileContentCache` keeps its two-tier routing (memory vs. disk) — only the disk tier's internal storage strategy changes from `DiskStorageStrategy` to `ChunkedDiskStorageStrategy`.

**Key Innovation**: Two-level concurrency — a single sequential extraction task writes chunks to a sparse NTFS file, while multiple concurrent readers borrow `ChunkedStream` instances that independently read from completed chunk regions. Chunk completion is signaled via `TaskCompletionSource<bool>` per chunk, enabling zero-polling await semantics.

**Related Documents**:
- [`CACHING_DESIGN.md`](CACHING_DESIGN.md) — File content cache architecture (GenericCache, storage strategies, borrow/return)
- [`CONCURRENCY_STRATEGY.md`](CONCURRENCY_STRATEGY.md) — Three-layer concurrency + RefCount
- [`STREAMING_ZIP_READER_DESIGN.md`](STREAMING_ZIP_READER_DESIGN.md) — ZIP reader and SubStream

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Design Goals and Non-Goals](#2-design-goals-and-non-goals)
3. [Architecture Overview](#3-architecture-overview)
4. [Tier Routing](#4-tier-routing)
5. [ChunkedDiskStorageStrategy](#5-chunkeddiskstoragestorage-strategy)
6. [ChunkedFileEntry — The Chunk Orchestrator](#6-chunkedfileentry--the-chunk-orchestrator)
7. [ChunkedStream — The Reader Interface](#7-chunkedstream--the-reader-interface)
8. [Concurrency Model](#8-concurrency-model)
9. [Integration with GenericCache and RefCount](#9-integration-with-genericcache-and-refcount)
10. [Lifecycle and Eviction](#10-lifecycle-and-eviction)
11. [Compression Method Handling](#11-compression-method-handling)
12. [Configuration](#12-configuration)
13. [Telemetry](#13-telemetry)
14. [Error Handling](#14-error-handling)
15. [Testing Strategy](#15-testing-strategy)
16. [Performance Analysis](#16-performance-analysis)
17. [Implementation Phases](#17-implementation-phases)
18. [Risks and Mitigations](#18-risks-and-mitigations)
19. [Alternatives Considered](#19-alternatives-considered)

---

## 1. Problem Statement

### 1.1 Current Extraction Flow for Large Files

Today, when a user reads from a large file inside a mounted ZIP archive:

```
FileContentCache.ReadAsync(archivePath, entry, cacheKey, buffer, offset)
  │
  ├─ entry.UncompressedSize >= 50MB → route to _diskCache
  │
  └─ _diskCache.BorrowAsync(cacheKey, ttl, factory)
       │
       ├─ Cache HIT → instant (MMF view stream)
       │
       └─ Cache MISS → DiskStorageStrategy.MaterializeAsync(factory)
            │
            ├─ factory() → ZipReader.OpenEntryStreamAsync(entry)
            │              → DeflateStream wrapping SubStream
            │
            ├─ result.Value.CopyToAsync(tempFile)  ← BLOCKS UNTIL 100% DONE
            │     5GB @ ~200MB/s ≈ 25 seconds
            │
            ├─ MemoryMappedFile.CreateFromFile(tempPath)
            │
            └─ return StoredEntry(DiskCacheEntry)
```

The critical bottleneck is `CopyToAsync(tempFile)` in `DiskStorageStrategy.MaterializeAsync()` (line 66 of `DiskStorageStrategy.cs`). It pipes the *entire* decompressed stream to disk before returning the `StoredEntry`. The `GenericCache.MaterializeAndCacheAsync()` awaits this call, meaning `BorrowAsync()` cannot return a handle until the full file is materialized.

### 1.2 Impact by File Size

| File Size | Decompression Time (~200MB/s) | User Experience |
|-----------|-------------------------------|-----------------|
| 50 MB     | ~250ms                        | Acceptable      |
| 200 MB    | ~1s                           | Noticeable lag   |
| 500 MB    | ~2.5s                         | Frustrating      |
| 1 GB      | ~5s                           | Poor             |
| 5 GB      | ~25s                          | Unusable         |

### 1.3 Real-World Scenarios

- **Video playback**: User double-clicks a 2GB `.mkv` in the mounted drive. VLC/MPC-HC waits 10 seconds for the first frame. With chunking: first frame in ~50ms.
- **Development archives**: Large binary assets in game development ZIPs. IDE opens a 500MB texture file — 2.5 second delay per open. With chunking: near-instant for sequential access.
- **Backup browsing**: User mounts a backup ZIP to find a specific file. Large files are effectively inaccessible.

---

## 2. Design Goals and Non-Goals

### Goals

1. **First-byte latency**: Serve the first read within ~50ms (one chunk decompression time) instead of waiting for full extraction
2. **Transparent integration**: `ChunkedStream` implements `Stream` — callers (`ZipVirtualFileSystem`, `DokanFileSystemAdapter`) see no difference
3. **Zero changes to GenericCache**: `ChunkedDiskStorageStrategy` fits within `IStorageStrategy<Stream>`, reusing all existing concurrency, eviction, RefCount, and telemetry machinery
4. **Concurrent reads during extraction**: Multiple readers can access completed chunks while the background extraction task continues writing subsequent chunks
5. **Data integrity**: Never serve stale, partial, or zero-filled data from unextracted sparse file regions

### Non-Goals

1. **Random seek optimization for Deflate**: Deflate requires sequential decompression. Seeking to an unextracted chunk requires decompressing everything before it. This is a fundamental compression constraint, not a design limitation.
2. **Individual chunk eviction**: Chunks are not independently evictable. The entire file entry is evicted as a unit (consistent with existing `DiskStorageStrategy` behavior).
3. **Resumable extraction**: If an entry is evicted mid-extraction, re-accessing the file starts extraction from scratch. Deflate state cannot be checkpointed.
4. **Write support**: Chunked extraction is read-only (consistent with ZipDrive's overall read-only design).

---

## 3. Architecture Overview

### 3.1 Two-Tier File Size Routing (Unchanged)

The routing logic in `FileContentCache` is **unchanged** — it remains a simple two-tier split based on `SmallFileCutoffBytes`. Only the disk tier's internal storage strategy is swapped:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    FileContentCache — Tier Routing (unchanged)               │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  entry.UncompressedSize < SmallFileCutoffBytes (default 50MB)              │
│  ─────────────────────────────────────────────────────────────►             │
│  Memory Tier: MemoryStorageStrategy → byte[] in RAM                        │
│  (existing, unchanged)                                                     │
│                                                                             │
│  entry.UncompressedSize >= SmallFileCutoffBytes                            │
│  ─────────────────────────────────────────────────────────────►             │
│  Disk Tier: ChunkedDiskStorageStrategy → sparse file + incremental         │
│  (REPLACES DiskStorageStrategy — all disk-tier files benefit from          │
│   incremental extraction, no size threshold)                               │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

A 50MB file at the cutoff boundary produces just 5 chunks — negligible overhead. A 5GB file produces 500 chunks. The same code path handles both.

### 3.2 Component Hierarchy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         FileContentCache                                     │
│  (Owns extraction pipeline, tier routing, IFileContentCache implementation) │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌──────────────────────────────┐  ┌──────────────────────────────────┐   │
│  │  GenericCache<Stream>         │  │  GenericCache<Stream>             │   │
│  │  name: "memory"               │  │  name: "disk"                    │   │
│  │                               │  │                                  │   │
│  │  MemoryStorageStrategy        │  │  ChunkedDiskStorageStrategy      │   │
│  │  (existing, unchanged)        │  │  (REPLACES DiskStorageStrategy)  │   │
│  │                               │  │                                  │   │
│  │  StoredEntry: byte[]          │  │  StoredEntry: ChunkedFileEntry   │   │
│  │  Retrieve → MemoryStream      │  │  Retrieve → ChunkedStream       │   │
│  └──────────────────────────────┘  └──────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 3.3 New Components (Replace DiskStorageStrategy + DiskCacheEntry)

| Component | Type | Replaces | Purpose |
|-----------|------|----------|---------|
| `ChunkedDiskStorageStrategy` | `IStorageStrategy<Stream>` | `DiskStorageStrategy` | Materializes entries incrementally; returns `ChunkedStream` on `Retrieve()` |
| `ChunkedFileEntry` | Internal record | `DiskCacheEntry` | Owns sparse backing file, chunk state, extraction task, and `CancellationTokenSource` |
| `ChunkedStream` | `Stream` subclass | `MemoryMappedViewStream` (returned by old `Retrieve()`) | Read-only stream that maps reads to chunks, blocking on unextracted regions |

---

## 4. Tier Routing

### 4.1 FileContentCache.ReadAsync — Unchanged Routing

The routing logic is **unchanged**. The only difference is what storage strategy backs `_diskCache`:

```csharp
// FileContentCache.ReadAsync — routing logic is identical to today
GenericCache<Stream> cache = entry.UncompressedSize < _cutoffBytes
    ? _memoryCache    // MemoryStorageStrategy (byte[])
    : _diskCache;     // ChunkedDiskStorageStrategy (was DiskStorageStrategy)

using ICacheHandle<Stream> handle = await cache.BorrowAsync(
    cacheKey, _defaultTtl, factory, cancellationToken);
```

### 4.2 Why Replace the Disk Tier (Not Add a Third Tier)

| Consideration | Replace Disk Tier (Chosen) | Third Tier |
|---------------|---------------------------|------------|
| Architecture | Clean — two tiers, no extra threshold | Three tiers, extra `ChunkedExtractionThresholdMb` config |
| Benefit scope | ALL disk-tier files get incremental extraction | Only files above an arbitrary threshold |
| Overhead for small disk files | 50MB file = 5 chunks, ~400 bytes TCS overhead — trivial | Zero (but misses the latency improvement) |
| Code surface | Replace `DiskStorageStrategy` constructor call in `FileContentCache` | Add third `GenericCache<Stream>`, third routing branch, third cleanup/eviction path |
| First-byte improvement | 50MB: 250ms → 50ms. 200MB: 1s → 50ms. 5GB: 25s → 50ms | Only files >= threshold get the benefit |

The overhead of `ChunkedDiskStorageStrategy` for a 50MB file is negligible:
- 5 `TaskCompletionSource<bool>` objects = ~400 bytes
- 1 `BitArray` of 5 bits = ~32 bytes
- 1 sparse file (same as the old temp file, just NTFS-sparse)
- Background extraction of 5 chunks completes in ~250ms total

Every disk-tier file benefits from first-byte latency improvement. No reason to gate it behind a threshold.

---

## 5. ChunkedDiskStorageStrategy

### 5.1 Interface Compliance

`ChunkedDiskStorageStrategy` implements `IStorageStrategy<Stream>` — the same interface used by `MemoryStorageStrategy` and the `DiskStorageStrategy` it replaces:

```csharp
public interface IStorageStrategy<TValue>
{
    Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<TValue>>> factory,
        CancellationToken cancellationToken);

    TValue Retrieve(StoredEntry stored);
    void Dispose(StoredEntry stored);
    bool RequiresAsyncCleanup { get; }
}
```

### 5.2 MaterializeAsync — The Critical Path

Unlike `DiskStorageStrategy.MaterializeAsync()` which awaits full extraction, the chunked strategy returns **after the first chunk is extracted**:

```
ChunkedDiskStorageStrategy.MaterializeAsync(factory, ct):
  │
  ├─ 1. Call factory(ct) → get DeflateStream + ZipReader lifecycle
  │
  ├─ 2. Create sparse temp file (NTFS, instant — no disk allocation)
  │     Path: {tempDir}/ZipDrive-{pid}/{guid}.zip2vd.chunked
  │     Size: entry.UncompressedSize (sparse — physical size = 0)
  │
  ├─ 3. Create ChunkedFileEntry:
  │     ├── BackingFilePath: string
  │     ├── FileSize: long (total uncompressed size)
  │     ├── ChunkSize: int (default 10MB)
  │     ├── ChunkCount: int (ceil(FileSize / ChunkSize))
  │     ├── ChunkCompletions: TaskCompletionSource<bool>[] — one per chunk
  │     ├── ChunkBitmap: BitArray — true = ready
  │     ├── ExtractionCts: CancellationTokenSource
  │     ├── ExtractionTask: Task (the background extraction)
  │     └── BytesExtracted: long (volatile, for telemetry)
  │
  ├─ 4. Start background extraction task (fires and forgets):
  │     ├── Open FileStream on sparse file (WriteThrough)
  │     ├── Read from DeflateStream in ChunkSize increments
  │     ├── Write each chunk to correct offset in sparse file
  │     ├── Set ChunkBitmap[i] = true
  │     ├── Signal ChunkCompletions[i].SetResult(true)
  │     └── On completion: dispose DeflateStream + ZipReader via OnDisposed
  │
  ├─ 5. Await first chunk completion:
  │     await ChunkCompletions[0].Task          ← ~50ms for 10MB
  │
  └─ 6. Return StoredEntry(ChunkedFileEntry, sizeBytes: FileSize)
         ↑ Reports FULL uncompressed size for capacity accounting
```

**Critical Design Decision**: `MaterializeAsync` reports the *full* `UncompressedSize` to `GenericCache` for capacity tracking, even though only the first chunk is on disk. This is correct because:
- The sparse file will eventually consume the full size
- The cache must plan capacity for the final state, not the current state
- Under-reporting would allow over-admission (multiple large files, then all expand)
- NTFS sparse files allocate physical clusters lazily, so the OS handles the actual disk usage correctly

### 5.3 Retrieve — Return a ChunkedStream

```csharp
public Stream Retrieve(StoredEntry stored)
{
    ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
    return new ChunkedStream(entry);
}
```

Each `Retrieve()` call creates a **fresh** `ChunkedStream` instance with its own `FileStream` handle and position tracking. This is identical to how `DiskStorageStrategy.Retrieve()` creates a fresh `MemoryMappedViewStream` per caller — concurrent borrowers never share stream state.

### 5.4 Dispose — Cancel Extraction + Cleanup

```csharp
public void Dispose(StoredEntry stored)
{
    ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;

    // 1. Cancel background extraction (if still running)
    entry.ExtractionCts.Cancel();

    // 2. Wait briefly for extraction task to observe cancellation
    //    (best-effort; don't block indefinitely)
    entry.ExtractionTask.Wait(TimeSpan.FromSeconds(2));

    // 3. Dispose internal resources
    entry.Dispose();
    //   ├── Dispose ExtractionCts
    //   ├── Cancel all pending ChunkCompletions with TrySetCanceled()
    //   └── Delete backing temp file
}
```

`RequiresAsyncCleanup => true` — the backing file may be large and deletion can be slow. Evicted entries are queued in `GenericCache._pendingCleanup` and processed by `CacheMaintenanceService`, identical to the existing `DiskStorageStrategy` pattern.

---

## 6. ChunkedFileEntry — The Chunk Orchestrator

### 6.1 Data Structure

```csharp
internal sealed class ChunkedFileEntry : IDisposable
{
    // === Immutable Configuration ===
    public string BackingFilePath { get; }
    public long FileSize { get; }
    public int ChunkSize { get; }
    public int ChunkCount { get; }

    // === Chunk Tracking ===
    private readonly BitArray _chunkBitmap;                    // true = ready
    private readonly TaskCompletionSource<bool>[] _chunkCompletions;

    // === Extraction Lifecycle ===
    public CancellationTokenSource ExtractionCts { get; }
    public Task ExtractionTask { get; internal set; }

    // === Telemetry ===
    private long _bytesExtracted;  // Volatile read/write
    public long BytesExtracted => Volatile.Read(ref _bytesExtracted);
    public double ExtractionProgress => FileSize == 0 ? 1.0
        : (double)BytesExtracted / FileSize;
}
```

### 6.2 Chunk Index Calculation

```csharp
// Given a byte offset, which chunk contains it?
public int GetChunkIndex(long offset)
    => (int)(offset / ChunkSize);

// Given a chunk index, what's the byte offset in the backing file?
public long GetChunkOffset(int chunkIndex)
    => (long)chunkIndex * ChunkSize;

// Given a chunk index, how many bytes does this chunk contain?
public int GetChunkLength(int chunkIndex)
    => chunkIndex < ChunkCount - 1
        ? ChunkSize
        : (int)(FileSize - (long)chunkIndex * ChunkSize);  // Last chunk may be smaller
```

### 6.3 Chunk State Machine

```
                ┌─────────┐
                │  Empty   │  ChunkBitmap[i] = false
                │          │  ChunkCompletions[i] = pending TCS
                └────┬─────┘
                     │  Background extraction reaches chunk i:
                     │  Write ChunkSize bytes to backing file at offset i*ChunkSize
                     │  Set ChunkBitmap[i] = true
                     │  Signal ChunkCompletions[i].SetResult(true)
                     ▼
               ┌───────────┐
               │   Ready    │  ChunkBitmap[i] = true
               │            │  ChunkCompletions[i].Task = completed
               └───────────┘

No "Materializing" intermediate state visible to readers.
Readers await the TCS — they don't poll.
```

### 6.4 Background Extraction Task

```csharp
private async Task ExtractAsync(
    Stream decompressedStream,
    Func<ValueTask>? onDisposed,
    CancellationToken cancellationToken)
{
    byte[] buffer = new byte[ChunkSize];

    try
    {
        await using FileStream fs = new FileStream(
            BackingFilePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,          // Allow concurrent reads!
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        for (int i = 0; i < ChunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunkLength = GetChunkLength(i);
            int totalRead = 0;

            // Read exactly chunkLength bytes from decompressed stream
            while (totalRead < chunkLength)
            {
                int read = await decompressedStream.ReadAsync(
                    buffer.AsMemory(totalRead, chunkLength - totalRead),
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    break; // Premature EOF

                totalRead += read;
            }

            // Write chunk to backing file at correct offset
            fs.Position = GetChunkOffset(i);
            await fs.WriteAsync(buffer.AsMemory(0, totalRead), cancellationToken)
                .ConfigureAwait(false);
            await fs.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Mark chunk as ready (order matters: write → bitmap → signal)
            _chunkBitmap[i] = true;                          // Volatile write
            Interlocked.Add(ref _bytesExtracted, totalRead); // Telemetry
            _chunkCompletions[i].TrySetResult(true);         // Wake up waiters
        }
    }
    catch (OperationCanceledException)
    {
        // Extraction cancelled (eviction or shutdown) — signal remaining chunks
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkCompletions[i].TrySetCanceled();
        }
    }
    catch (Exception ex)
    {
        // Extraction failed — signal remaining chunks with exception
        for (int i = 0; i < ChunkCount; i++)
        {
            _chunkCompletions[i].TrySetException(ex);
        }
    }
    finally
    {
        // Dispose the decompressed stream and ZipReader
        if (decompressedStream is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (decompressedStream is IDisposable disposable)
            disposable.Dispose();

        if (onDisposed is not null)
            await onDisposed().ConfigureAwait(false);
    }
}
```

**Key details**:

1. **`FileShare.Read`** on the writing `FileStream` allows concurrent `ChunkedStream` readers to open their own `FileStream` handles for reading.
2. **Flush after each chunk** ensures data is on disk before signaling completion. Without flush, a reader might see the bitmap set but read stale buffer cache data.
3. **Signal order**: `bitmap → bytesExtracted → TCS`. The bitmap is the authoritative state; the TCS is the notification mechanism. A reader that checks the bitmap before the TCS fires still gets the correct answer.
4. **Error propagation**: If extraction fails mid-stream (corrupt ZIP, I/O error), all pending TCS entries receive the exception. Readers awaiting those chunks get the exception propagated through their `await`.
5. **Resource lifecycle**: The `DeflateStream` and `ZipReader` (via `OnDisposed` callback from `CacheFactoryResult`) are disposed in the `finally` block, regardless of success or failure. This is critical because the `ZipReader` holds an open `FileStream` on the ZIP archive.

---

## 7. ChunkedStream — The Reader Interface

### 7.1 Design

`ChunkedStream` implements `Stream` and transparently handles the blocking-on-unextracted-chunks logic. Callers (including `FileContentCache.ReadAsync`, `DokanFileSystemAdapter.ReadFile`) see a normal seekable stream.

```csharp
internal sealed class ChunkedStream : Stream
{
    private readonly ChunkedFileEntry _entry;
    private readonly FileStream _readStream;   // Opened with FileShare.ReadWrite
    private long _position;
    private bool _disposed;

    public ChunkedStream(ChunkedFileEntry entry)
    {
        _entry = entry;
        _readStream = new FileStream(
            entry.BackingFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,    // Extraction writes concurrently
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _entry.FileSize;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }
}
```

### 7.2 Read Implementation — The Core Logic

```csharp
public override async ValueTask<int> ReadAsync(
    Memory<byte> buffer, CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(_disposed, this);

    long remaining = _entry.FileSize - _position;
    if (remaining <= 0)
        return 0;

    int bytesToRead = (int)Math.Min(buffer.Length, remaining);
    int totalRead = 0;

    while (totalRead < bytesToRead)
    {
        long currentOffset = _position + totalRead;
        int chunkIndex = _entry.GetChunkIndex(currentOffset);

        // ══════════════════════════════════════════════════════════
        // CRITICAL: Ensure chunk is ready before reading
        // Without this check, we'd read zeros from the sparse file
        // ══════════════════════════════════════════════════════════
        await EnsureChunkReadyAsync(chunkIndex, cancellationToken)
            .ConfigureAwait(false);

        // Calculate how many bytes we can read from this chunk
        long chunkStart = _entry.GetChunkOffset(chunkIndex);
        int chunkLength = _entry.GetChunkLength(chunkIndex);
        int offsetInChunk = (int)(currentOffset - chunkStart);
        int bytesAvailableInChunk = chunkLength - offsetInChunk;
        int bytesFromThisChunk = Math.Min(bytesToRead - totalRead, bytesAvailableInChunk);

        // Read from backing file
        _readStream.Position = currentOffset;
        int read = await _readStream.ReadAsync(
            buffer.Slice(totalRead, bytesFromThisChunk),
            cancellationToken).ConfigureAwait(false);

        if (read == 0)
            break; // Unexpected EOF in backing file

        totalRead += read;
    }

    _position += totalRead;
    return totalRead;
}
```

### 7.3 EnsureChunkReadyAsync — The Blocking Gate

```csharp
private async ValueTask EnsureChunkReadyAsync(
    int chunkIndex, CancellationToken cancellationToken)
{
    // Fast path: chunk already extracted (lock-free BitArray check)
    if (_entry.IsChunkReady(chunkIndex))
        return;

    // Slow path: await the chunk's TaskCompletionSource
    // This blocks until the background extraction task signals completion
    // for this specific chunk, or throws if extraction failed/was cancelled.
    await _entry.WaitForChunkAsync(chunkIndex, cancellationToken)
        .ConfigureAwait(false);
}
```

Where `ChunkedFileEntry` provides:

```csharp
public bool IsChunkReady(int chunkIndex) => _chunkBitmap[chunkIndex];

public Task WaitForChunkAsync(int chunkIndex, CancellationToken cancellationToken)
{
    // If already done, return immediately (hot path after first check)
    Task completionTask = _chunkCompletions[chunkIndex].Task;
    if (completionTask.IsCompleted)
        return completionTask;

    // Register cancellation to avoid waiting forever if caller cancels
    return WaitWithCancellationAsync(completionTask, cancellationToken);
}

private static async Task WaitWithCancellationAsync(
    Task completionTask, CancellationToken cancellationToken)
{
    using CancellationTokenRegistration registration = cancellationToken.Register(
        () => { /* No-op: just allows WhenAny to pick up cancellation */ });

    Task cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
    Task completed = await Task.WhenAny(completionTask, cancelTask).ConfigureAwait(false);

    cancellationToken.ThrowIfCancellationRequested();
    await completionTask.ConfigureAwait(false); // Propagate exceptions
}
```

### 7.4 Cross-Chunk Boundary Reads

A single `Read(offset, count)` call can span multiple chunks. Example:

```
ChunkSize = 10MB
Read(offset = 9.5MB, count = 2MB)

  Chunk 0          Chunk 1          Chunk 2
  [─────10MB─────] [─────10MB─────] [─────10MB─────]
              ├── 0.5MB ──┤── 1.5MB ──┤
              ↑ offset=9.5MB          ↑ read ends at 11.5MB
```

The `while (totalRead < bytesToRead)` loop in `ReadAsync` handles this naturally:
1. First iteration: `chunkIndex=0`, reads 0.5MB from end of chunk 0
2. Second iteration: `chunkIndex=1`, ensures chunk 1 ready, reads 1.5MB from start of chunk 1

### 7.5 Synchronous Read Fallback

DokanNet's `ReadFile` callback may invoke synchronous `Stream.Read()`. The `ChunkedStream` must handle this:

```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    // Block synchronously on the async path.
    // This is safe because:
    // 1. DokanNet thread pool is separate from the extraction task's thread pool
    // 2. The extraction task uses I/O completion port threads (IOCP), not thread pool
    // 3. No risk of deadlock as long as extraction uses async I/O
    return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
        .AsTask().GetAwaiter().GetResult();
}
```

### 7.6 Dispose

```csharp
protected override void Dispose(bool disposing)
{
    if (!_disposed && disposing)
    {
        _readStream.Dispose();
        _disposed = true;
    }
    base.Dispose(disposing);
}
```

Note: `ChunkedStream.Dispose()` only closes this reader's `FileStream`. It does **not** dispose the `ChunkedFileEntry` — that is owned by `ChunkedDiskStorageStrategy.Dispose()`, which is called by `GenericCache` during eviction.

---

## 8. Concurrency Model

### 8.1 Five-Layer Concurrency for Chunked Extraction

The disk tier (now backed by `ChunkedDiskStorageStrategy`) extends the existing three-layer concurrency model with two additional layers specific to incremental extraction:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                   Five-Layer Concurrency Strategy                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  Layer 1: Lock-Free Cache Hit (existing, unchanged)                        │
│  ───────────────────────────────────────────────────                        │
│  ConcurrentDictionary.TryGetValue() → < 100ns                             │
│  If entry exists and not expired → IncrementRefCount → return handle       │
│                                                                             │
│  Layer 2: Per-Key Materialization (existing, unchanged)                    │
│  ──────────────────────────────────────────────────                         │
│  Lazy<Task<CacheEntry>> ensures only ONE ChunkedDiskStorageStrategy        │
│  .MaterializeAsync() runs per cache key. 10 threads requesting the         │
│  same uncached file → 1 materialization, 1 sparse file, 1 extraction.      │
│                                                                             │
│  Layer 3: Eviction Lock (existing, unchanged)                              │
│  ────────────────────────────────────────────                               │
│  Global lock only when capacity exceeded. Does not block reads.            │
│  Only evicts entries with RefCount = 0.                                    │
│                                                                             │
│  Layer 4: RefCount Protection (existing, unchanged)                        │
│  ──────────────────────────────────────────────                             │
│  Borrowed entries (RefCount > 0) are protected from eviction.              │
│  Each ChunkedStream borrower increments RefCount.                          │
│  Dispose() decrements RefCount → allows eviction when all released.        │
│                                                                             │
│  Layer 5: Per-Chunk Completion Signaling (NEW)                             │
│  ──────────────────────────────────────────────                             │
│  TaskCompletionSource<bool>[] — one per chunk.                             │
│  Background extraction task: signals TCS[i] when chunk i is written.       │
│  Multiple readers: await TCS[i].Task for their respective chunks.          │
│  No polling. No per-chunk locks. Pure async signaling.                     │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 8.2 Concurrency Scenarios

#### Scenario A: 10 Threads Request Same Uncached 5GB File

```
Time    Thread 1           Thread 2           Thread 3-10
─────   ─────────────      ─────────────      ───────────────
t=0     BorrowAsync()      BorrowAsync()      BorrowAsync()
        │ Cache MISS       │ Cache MISS       │ Cache MISS
        │                  │                  │
t=0     Lazy<Task> wins    Lazy<Task> waits   Lazy<Task> wait
        MaterializeAsync() ...                 ...
        │ Create sparse file
        │ Start extraction task
        │ Await chunk 0 (~50ms)
        │
t=50ms  Chunk 0 ready!
        MaterializeAsync returns StoredEntry
        ├── GenericCache stores entry
        ├── IncrementRefCount (temporary hold)
        ├── Post-store eviction check
        └── DecrementRefCount (release hold)
        │
        All 10 threads receive CacheEntry from Lazy<Task>
        Each: IncrementRefCount → Retrieve() → new ChunkedStream
        │
t=50ms  RefCount = 10      RefCount = 10     RefCount = 10
        Read(offset=0)     Read(offset=0)     Read(offset=0)
        │ Chunk 0 ready    │ Chunk 0 ready    │ Chunk 0 ready
        │ Instant read     │ Instant read     │ Instant read
        │
t=60ms  Read(offset=15MB)  Read(offset=5MB)
        │ Chunk 1 ready?   │ Chunk 0 still
        │ Maybe — depends  │ Instant
        │ on extraction    │
        │ speed            │
        │
        Background extraction continues writing chunks 1, 2, 3...
        All 10 ChunkedStream instances read from same backing file
```

**Key**: Only 1 materialization. Only 1 extraction task. 10 readers share the same `ChunkedFileEntry`. Each has their own `ChunkedStream` with independent `FileStream` and position.

#### Scenario B: Reader Requests Chunk Not Yet Extracted

```
State:   [✅ 0] [✅ 1] [✅ 2] [🔄 3] [⬜ 4] ... [⬜ 499]

Reader A: Read(offset = 35MB)
  → chunkIndex = 3
  → IsChunkReady(3) = false
  → await WaitForChunkAsync(3)
  → Background extraction finishes chunk 3
  → TCS[3].SetResult(true) → wakes Reader A
  → Reader A reads from backing file at offset 35MB
  → ~100ms wait (time to extract chunk 3)

Reader B: Read(offset = 5MB) (concurrent with Reader A)
  → chunkIndex = 0
  → IsChunkReady(0) = true
  → No wait — instant read from backing file
```

#### Scenario C: Reader Seeks Far Ahead (e.g., Video Scrub to 50%)

```
5GB file, Deflate compressed:
  ChunkSize = 10MB, ChunkCount = 500
  Reader seeks to offset 2.5GB → chunkIndex = 250

State:   [✅ 0..10] [⬜ 11..250] ... [⬜ 499]

Reader: Read(offset = 2.5GB)
  → chunkIndex = 250
  → IsChunkReady(250) = false
  → await WaitForChunkAsync(250)
  │
  │  Background extraction is sequentially decompressing Deflate:
  │  Must decompress chunks 11, 12, 13, ... 249, 250 before chunk 250 is ready
  │  At ~200MB/s: (250 - 10) × 10MB = 2.4GB → ~12 seconds
  │
  └─ Reader waits ~12 seconds for extraction to reach chunk 250

This is no worse than today (today: wait for full 5GB = 25 seconds).
With chunking: wait for 2.5GB = ~12.5 seconds (50% of today's wait).
And: chunks 0-10 were already available instantly for any earlier reads.
```

**This is the fundamental Deflate constraint**. Chunking cannot skip ahead in a Deflate stream. But it's still strictly better than the status quo because:
1. Sequential reads from the start get first-byte latency of ~50ms (vs. 25s today)
2. Mid-file seeks wait proportionally (vs. full-file wait today)
3. Once extracted, all chunks are instant on re-read

#### Scenario D: Extraction Cancelled Mid-Stream (Eviction)

```
State:   [✅ 0..50] [🔄 51] [⬜ 52..499]

Eviction triggered → entry.RefCount drops to 0 → GenericCache evicts
  → ChunkedDiskStorageStrategy.Dispose(stored)
  → entry.ExtractionCts.Cancel()
  │
  │ Background extraction task:
  │ → OperationCanceledException thrown
  │ → for remaining chunks: TCS[i].TrySetCanceled()
  │ → finally: dispose DeflateStream + ZipReader
  │
  │ Any ChunkedStream reader awaiting TCS[52+]:
  │ → TaskCanceledException propagated
  │ → Reader handles exception (Dokan returns error to caller)
  │
  └─ Backing file queued for deletion in _pendingCleanup

No data corruption. No dangling resources. Clean cancellation chain.
```

### 8.3 Thread Safety Analysis

| Resource | Writer | Reader(s) | Synchronization |
|----------|--------|-----------|-----------------|
| Sparse backing file | Extraction task (`FileAccess.Write`, `FileShare.Read`) | N `ChunkedStream` instances (`FileAccess.Read`, `FileShare.ReadWrite`) | OS file sharing + chunk gate |
| `_chunkBitmap[i]` | Extraction task (set true) | N readers (check) | Write-once semantics; readers always check TCS if bitmap is false |
| `_chunkCompletions[i]` | Extraction task (`.TrySetResult`) | N readers (`.Task`) | `TaskCompletionSource` is thread-safe by design |
| `_bytesExtracted` | Extraction task (`Interlocked.Add`) | Telemetry (`Volatile.Read`) | Interlocked/Volatile |
| `ChunkedStream._position` | Single reader per stream | Same reader | Not shared (each borrower gets own `ChunkedStream`) |
| `ChunkedStream._readStream` | Single reader per stream | Same reader | Not shared (each borrower gets own `FileStream`) |

**Data corruption safeguard**: The `ChunkedStream.EnsureChunkReadyAsync()` call is the **load-bearing safety gate**. If a reader attempts to read from offset X, it MUST verify that chunk `X / ChunkSize` is ready before issuing a `FileStream.Read`. Without this check, the reader would read zeros from the unallocated sparse file region, silently returning corrupt data. The `BitArray` check + `TaskCompletionSource` await guarantees this.

---

## 9. Integration with GenericCache and RefCount

### 9.1 Zero Changes to GenericCache

The replacement requires **no modifications** to `GenericCache<T>`. Here's why each existing mechanism works as-is:

| GenericCache Feature | How ChunkedDiskStorageStrategy Uses It | Changes Needed |
|---------------------|----------------------------------------|----------------|
| `BorrowAsync()` Layer 1 (lock-free hit) | Cache hit → `IncrementRefCount` → `Retrieve()` returns fresh `ChunkedStream` | None |
| `BorrowAsync()` Layer 2 (`Lazy<Task>`) | Prevents duplicate `ChunkedDiskStorageStrategy.MaterializeAsync()` — only one extraction per file | None |
| `MaterializeAndCacheAsync()` | Awaits `strategy.MaterializeAsync()` — returns after first chunk extracted | None |
| `StoredEntry.SizeBytes` | Reports full uncompressed size — correct for capacity planning | None |
| `CacheEntry.RefCount` | Incremented per borrower, decremented on `CacheHandle.Dispose()` | None |
| `_currentSizeBytes` | Tracks full file size at entry creation — conservative (correct) | None |
| Eviction (`EvictIfNeededAsync`) | Only evicts entries with `RefCount == 0` — entries with active readers are protected | None |
| `TryEvictEntry()` → `strategy.Dispose()` | Calls `ChunkedDiskStorageStrategy.Dispose()` → cancels extraction + deletes file | None |
| `_pendingCleanup` queue | Chunked strategy sets `RequiresAsyncCleanup = true` → evicted entries queued for background cleanup | None |
| `CacheTelemetry` metrics | Hit/miss/eviction counters tagged with `tier: "disk"` (unchanged tier name) | None |
| `CacheMaintenanceService` | Calls `EvictExpired()` and `ProcessPendingCleanup()` — both work unchanged | None |

### 9.2 RefCount Flow — Detailed Trace

```
═══════════════════════════════════════════════════════════════════════
TIMELINE: Two concurrent readers for the same uncached 5GB file
═══════════════════════════════════════════════════════════════════════

t=0ms   Reader A: BorrowAsync("archive.zip:video.mkv")
        → Layer 1: cache MISS
        → Layer 2: Lazy<Task> created, MaterializeAsync() starts
           → ChunkedDiskStorageStrategy.MaterializeAsync()
           → Create sparse file, start extraction task
           → Await first chunk...

t=5ms   Reader B: BorrowAsync("archive.zip:video.mkv")
        → Layer 1: cache MISS (entry not stored yet)
        → Layer 2: Lazy<Task> found! Awaits same Task

t=50ms  Extraction: Chunk 0 complete
        → MaterializeAsync returns StoredEntry(ChunkedFileEntry, 5GB)
        → GenericCache.MaterializeAndCacheAsync():
           entry = new CacheEntry(key, stored, now, ttl)
           entry.IncrementRefCount()          → RefCount = 1 (temporary hold)
           _cache[key] = entry
           _currentSizeBytes += 5GB
           EvictIfNeededAsync() runs          → entry protected (RefCount=1)
           entry.DecrementRefCount()          → RefCount = 0 (release hold)
        → return entry

t=50ms  Reader A: receives CacheEntry from Lazy<Task>.Value
        → entry.IncrementRefCount()           → RefCount = 1
        → Retrieve() → new ChunkedStream(entry)
        → return CacheHandle(entry, stream, Return)

t=50ms  Reader B: receives same CacheEntry from Lazy<Task>.Value
        → entry.IncrementRefCount()           → RefCount = 2
        → Retrieve() → new ChunkedStream(entry)  [different instance!]
        → return CacheHandle(entry, stream, Return)

t=50ms  _materializationTasks.TryRemove(key) — cleanup Lazy

        Background extraction continues writing chunks 1, 2, 3...

t=100ms Reader A: stream.Read(offset=0, count=64KB)
        → ChunkedStream.EnsureChunkReadyAsync(0) → already ready
        → FileStream.Read(0, 64KB) → instant
        → return 64KB

t=100ms Reader B: stream.Read(offset=15MB, count=64KB)
        → ChunkedStream.EnsureChunkReadyAsync(1) → maybe ready
        → if ready: instant read
        → if not: await TCS[1].Task (~100ms more)

t=500ms Reader A done: handle.Dispose()
        → ChunkedStream.Dispose() → close FileStream
        → Return(entry) → entry.DecrementRefCount() → RefCount = 1

t=800ms Reader B done: handle.Dispose()
        → ChunkedStream.Dispose() → close FileStream
        → Return(entry) → entry.DecrementRefCount() → RefCount = 0

        Entry now evictable (RefCount = 0)
        Background extraction may still be running (chunks 50+ being extracted)
        But entry is in cache → future BorrowAsync gets Layer 1 HIT

t=30min TTL expires → CacheMaintenanceService.EvictExpired()
        → entry.RefCount == 0 → evict
        → ChunkedDiskStorageStrategy.Dispose()
        → ExtractionCts.Cancel() (extraction likely already finished)
        → Delete backing file → queued in _pendingCleanup
```

### 9.3 Edge Case: Eviction During Active Extraction

```
═══════════════════════════════════════════════════════════════════════
SCENARIO: Cache capacity exceeded while extraction is running
═══════════════════════════════════════════════════════════════════════

State:
  _diskCache capacity: 10GB
  _currentSizeBytes: 8GB (existing entries)
  New entry: 5GB video → stored with SizeBytes=5GB → _currentSizeBytes = 13GB

GenericCache.MaterializeAndCacheAsync():
  → entry.IncrementRefCount()  → RefCount=1 (temporary hold)
  → _cache[key] = entry
  → _currentSizeBytes = 13GB > 10GB capacity
  → EvictIfNeededAsync(0):
      │ Lock acquired
      │ Need to free ~3GB
      │ Select victims where RefCount == 0
      │ Evict other entries (not the new one — RefCount=1)
      │ Each eviction: TryEvictEntry → strategy.Dispose() → cancel + delete
  → entry.DecrementRefCount()  → RefCount=0

Caller: BorrowAsync() returns handle
  → entry.IncrementRefCount()  → RefCount=1
  → Reader uses ChunkedStream
  → Background extraction continues

If eviction needs MORE space and this entry has RefCount=0 later:
  → Entry is evictable
  → Eviction triggers Dispose → ExtractionCts.Cancel()
  → All pending TCS entries get TrySetCanceled()
  → Any reader awaiting a chunk gets TaskCanceledException
  → Reader's BorrowAsync handle was already disposed (RefCount=0)
  → No corruption — clean cancellation path
```

---

## 10. Lifecycle and Eviction

### 10.1 Entry Lifecycle

```
┌────────────────────────────────────────────────────────────────────────────┐
│                        ChunkedFileEntry Lifecycle                          │
├────────────────────────────────────────────────────────────────────────────┤
│                                                                            │
│  ┌──────────────────┐                                                     │
│  │   Created         │  MaterializeAsync():                               │
│  │                   │  - Sparse file created                             │
│  │  Extraction: 🔄   │  - First chunk extracted                          │
│  │  RefCount:  0     │  - StoredEntry returned to GenericCache            │
│  └────────┬──────────┘                                                    │
│           │ GenericCache stores entry                                     │
│           ▼                                                               │
│  ┌──────────────────┐                                                     │
│  │   Active          │  BorrowAsync() → RefCount > 0                     │
│  │                   │  Background extraction continues                   │
│  │  Extraction: 🔄   │  Readers access completed chunks                  │
│  │  RefCount:  1+    │  Protected from eviction                          │
│  └────────┬──────────┘                                                    │
│           │ All handles disposed → RefCount = 0                          │
│           │ Extraction may or may not be complete                        │
│           ▼                                                               │
│  ┌──────────────────┐                                                     │
│  │   Idle            │  In cache, evictable                               │
│  │                   │  Extraction may continue in background             │
│  │  Extraction: ✅/🔄│  (greedy — extract all chunks even if no reader)  │
│  │  RefCount:  0     │                                                    │
│  └────────┬──────────┘                                                    │
│           │                                                               │
│           ├─── BorrowAsync() → RefCount > 0 → back to Active            │
│           │                                                               │
│           ├─── TTL expires or eviction policy selects                    │
│           ▼                                                               │
│  ┌──────────────────┐                                                     │
│  │   Evicted         │  ChunkedDiskStorageStrategy.Dispose():            │
│  │                   │  - ExtractionCts.Cancel()                         │
│  │                   │  - Pending TCS entries → TrySetCanceled()         │
│  │                   │  - Backing file queued for deletion               │
│  └──────────────────┘                                                     │
│                                                                            │
└────────────────────────────────────────────────────────────────────────────┘
```

### 10.2 Greedy vs. Lazy Extraction

**Decision: Greedy** (continue extracting all chunks even after readers disconnect)

| Strategy | Pros | Cons |
|----------|------|------|
| **Greedy (recommended)** | Next access is instant; Simple (no pause/resume); Deflate can't resume from middle anyway | Consumes disk I/O + space for potentially unused chunks |
| Lazy (stop after last requested chunk) | Saves I/O for partially-accessed files | Must restart from byte 0 on next access past extracted range; Complex pause/resume logic |
| Hybrid (stop after idle timeout) | Balanced | Additional timer management; Deflate still can't resume |

Greedy is the right default because:
1. The TTL + eviction will reclaim disk space for unused entries
2. If the user opened the file once, sequential access pattern is likely (video playback, file copy)
3. Stopping a Deflate stream mid-extraction is irreversible — you can't resume from the middle
4. The disk I/O cost of continuing extraction is bounded by the file size (already accounted for in capacity)

### 10.3 Eviction Policy Interaction

The existing `LruEvictionPolicy` works unchanged. A `ChunkedFileEntry` in the cache appears as a normal `ICacheEntry` with:
- `SizeBytes` = full uncompressed size
- `LastAccessedAt` = updated on each `BorrowAsync` hit
- `RefCount` = number of active `CacheHandle<Stream>` borrowers

Partially-extracted entries are not distinguishable from fully-extracted entries at the eviction policy level. This is by design — the eviction policy shouldn't need to know about internal extraction state.

---

## 11. Compression Method Handling

### 11.1 Deflate (CompressionMethod = 8)

Standard chunked extraction as described above. Sequential decompression constraint applies — chunks must be extracted in order from chunk 0.

### 11.2 Store (CompressionMethod = 0)

Store-compressed entries have **no compression** — the data in the ZIP file is the raw, uncompressed bytes. This means:
- Random access IS possible by seeking directly within the ZIP file
- No need for a sparse file, no need for chunk extraction
- Could serve reads directly from the ZIP via `SubStream`

**Decision**: For this design, Store entries still go through the same chunked path. The chunk extraction for Store entries is trivially fast (raw file copy speed, no decompression CPU cost). The architectural simplification of a single code path outweighs the marginal performance gain of direct ZIP reads.

**Future optimization**: A `DirectReadStorageStrategy` that serves Store entries directly from the ZIP file (zero-copy) is a natural follow-up. It would bypass caching entirely for Store entries, trading disk seeks for zero extraction latency and zero disk space usage. This is a separate design.

---

## 12. Configuration

### 12.1 New CacheOptions Property

Only one new configuration property is needed. The disk tier capacity (`DiskCacheSizeMb`) and cutoff (`SmallFileCutoffMb`) are unchanged — the chunked strategy operates within the same capacity envelope as the old `DiskStorageStrategy`.

```csharp
public class CacheOptions
{
    // ... existing properties (unchanged) ...
    // MemoryCacheSizeMb, DiskCacheSizeMb, SmallFileCutoffMb,
    // TempDirectory, DefaultTtlMinutes, EvictionCheckIntervalSeconds

    /// <summary>
    /// Chunk size in megabytes for incremental disk-tier extraction.
    /// Tradeoff: smaller = lower first-byte latency, more TCS overhead.
    /// Default: 10 MB.
    /// </summary>
    public int ChunkSizeMb { get; set; } = 10;

    // Computed property
    internal int ChunkSizeBytes => ChunkSizeMb * 1024 * 1024;
}
```

### 12.2 Updated appsettings.jsonc

```jsonc
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,
    "DiskCacheSizeMb": 10240,
    "SmallFileCutoffMb": 50,
    "ChunkSizeMb": 10,                      // NEW — chunk size for disk tier
    "TempDirectory": null,
    "DefaultTtlMinutes": 30,
    "EvictionCheckIntervalSeconds": 60
  }
}
```

### 12.3 Chunk Size Tradeoff Analysis

| Chunk Size | First-Byte Latency (~200MB/s) | TCS Objects (5GB file) | Boundary-Cross Frequency |
|------------|-------------------------------|------------------------|--------------------------|
| 1 MB       | ~5ms                          | 5000                   | Very frequent             |
| 4 MB       | ~20ms                         | 1250                   | Moderate                  |
| **10 MB**  | **~50ms**                     | **500**                | **Low**                   |
| 32 MB      | ~160ms                        | 156                    | Rare                      |
| 64 MB      | ~320ms                        | 78                     | Very rare                 |

**10MB default rationale**: 50ms first-byte latency is imperceptible to users. 500 `TaskCompletionSource<bool>` objects consume ~40KB (negligible for a 5GB file). Chunk-boundary reads are infrequent with typical 64KB-4MB I/O sizes.

---

## 13. Telemetry

### 13.1 New Metrics

Extend `CacheTelemetry` with chunked-tier-specific instruments:

```csharp
// Counter: chunks extracted
internal static readonly Counter<long> ChunksExtracted =
    Meter.CreateCounter<long>("cache.chunks.extracted",
        unit: "{chunk}", description: "Number of chunks extracted");

// Counter: chunk waits (reader had to await extraction)
internal static readonly Counter<long> ChunkWaits =
    Meter.CreateCounter<long>("cache.chunks.waits",
        unit: "{wait}", description: "Number of times a reader waited for chunk extraction");

// Histogram: chunk extraction duration
internal static readonly Histogram<double> ChunkExtractionDuration =
    Meter.CreateHistogram<double>("cache.chunks.extraction_duration",
        unit: "ms", description: "Time to extract a single chunk");

// Histogram: chunk wait duration (reader perspective)
internal static readonly Histogram<double> ChunkWaitDuration =
    Meter.CreateHistogram<double>("cache.chunks.wait_duration",
        unit: "ms", description: "Time a reader waited for chunk extraction");

// Observable gauge: extraction progress per active entry
internal static readonly ObservableGauge<double> ExtractionProgress = ...;
```

### 13.2 Logging

```
[Information] ChunkedDiskStorageStrategy: Started chunked extraction for
    {Key} ({FileSize:F1}MB, {ChunkCount} chunks × {ChunkSize:F1}MB)

[Debug] ChunkedFileEntry: Chunk {ChunkIndex}/{ChunkCount} extracted
    ({ChunkSize:F1}MB, {Progress:P0} complete)

[Debug] ChunkedStream: Reader waited {WaitMs:F1}ms for chunk {ChunkIndex}

[Information] ChunkedDiskStorageStrategy: Extraction complete for
    {Key} ({FileSize:F1}MB, {ElapsedMs:F0}ms, {ThroughputMbps:F0}MB/s)

[Warning] ChunkedFileEntry: Extraction cancelled for {Key}
    ({ChunksCompleted}/{ChunkCount} chunks extracted)
```

---

## 14. Error Handling

### 14.1 Extraction Failure Mid-Stream

If the `DeflateStream` throws (corrupt ZIP data, I/O error on source archive) during chunk N extraction:

1. Extraction task catches the exception
2. All pending `TCS[N..ChunkCount-1]` receive `TrySetException(ex)`
3. Already-completed chunks (0..N-1) remain valid and readable
4. Readers awaiting chunk N+ get the exception propagated through `await TCS[i].Task`
5. The `ChunkedStream.ReadAsync()` propagates the exception to the caller
6. DokanNet adapter translates to appropriate `NtStatus` error code

### 14.2 Backing File I/O Error

If writing to the sparse backing file fails:

1. Same error propagation as above — `TrySetException` on remaining TCS entries
2. Partial backing file is cleaned up during eviction or shutdown

### 14.3 Reader Cancellation

If a reader's `CancellationToken` is cancelled while awaiting a chunk:

1. `WaitWithCancellationAsync` detects cancellation via `Task.WhenAny`
2. Throws `OperationCanceledException` to the reader
3. Does NOT cancel the extraction task — other readers may still need it
4. Reader's `CacheHandle<Stream>` is still properly disposed (via `using` or `finally`)

### 14.4 Shutdown

`FileContentCache.Clear()` → `_diskCache.Clear()` → for each entry:
1. Force-remove from cache (ignores RefCount — shutdown scenario only)
2. `ChunkedDiskStorageStrategy.Dispose(stored)` → cancel extraction + delete file
3. `DeleteCacheDirectory()` removes the entire temp directory

---

## 15. Testing Strategy

### 15.1 Unit Tests — ChunkedFileEntry

| Test | Description |
|------|-------------|
| `ChunkIndex_Calculation_Correct` | Verify `GetChunkIndex()` for various offsets including boundaries |
| `ChunkLength_LastChunk_Partial` | Last chunk may be smaller than `ChunkSize` |
| `ChunkCompletion_SignaledInOrder` | Chunks signal TCS in sequential order |
| `CancellationPropagated_ToPendingChunks` | Cancel extraction → remaining TCS get `TrySetCanceled()` |
| `ExtractionError_PropagatedToWaiters` | Exception in extraction → TCS get `TrySetException()` |
| `DoubleDispose_Safe` | Multiple `Dispose()` calls are no-op after first |

### 15.2 Unit Tests — ChunkedStream

| Test | Description |
|------|-------------|
| `Read_FromReadyChunk_Instant` | No await when chunk bitmap is true |
| `Read_FromPendingChunk_Waits` | Blocks until TCS signals, then reads correctly |
| `Read_CrossChunkBoundary_BothChunks` | Read spanning two chunks reads from both |
| `Read_BeyondEof_ReturnsZero` | Offset >= FileSize returns 0 bytes |
| `Seek_UpdatesPosition` | Position property tracks correctly |
| `ConcurrentReads_IndependentPositions` | Two ChunkedStreams on same entry don't interfere |
| `Read_AfterExtractionCancelled_Throws` | `TaskCanceledException` when chunk was cancelled |
| `Read_AfterExtractionError_Throws` | Exception propagated from failed extraction |
| `SyncRead_Fallback_Works` | `Stream.Read()` (non-async) works via `GetAwaiter().GetResult()` |

### 15.3 Unit Tests — ChunkedDiskStorageStrategy

| Test | Description |
|------|-------------|
| `MaterializeAsync_ReturnsAfterFirstChunk` | Does not wait for full extraction |
| `MaterializeAsync_ReportsFullSize` | `StoredEntry.SizeBytes` = full uncompressed size |
| `Retrieve_ReturnsFreshChunkedStream` | Each call returns different `ChunkedStream` instance |
| `Dispose_CancelsExtraction` | Cancellation token triggered, extraction task ends |
| `Dispose_DeletesBackingFile` | Temp file removed after disposal |
| `RequiresAsyncCleanup_True` | Returns `true` for background cleanup |

### 15.4 Integration Tests — Concurrent Access During Extraction

| Test | Description |
|------|-------------|
| `ThunderingHerd_ChunkedEntry_SingleExtraction` | 20 threads BorrowAsync same key → 1 MaterializeAsync, 1 extraction task |
| `ConcurrentReaders_DuringExtraction_AllGetCorrectData` | Readers access different offsets while extraction runs; SHA-256 verify each chunk |
| `ReadyChunks_ServedInstantly_WhileExtractionContinues` | Measure latency for ready chunks < 1ms while extraction is active |
| `Eviction_DuringExtraction_CleanCancellation` | Evict entry while extraction running → no leaks, no corruption |
| `RefCount_ProtectsFromEviction_DuringExtraction` | Entry with active readers not evicted even under capacity pressure |
| `PostEviction_NewBorrow_StartsNewExtraction` | After eviction, same key triggers fresh extraction |

### 15.5 Integration Tests — FileContentCache Routing

| Test | Description |
|------|-------------|
| `SmallFile_RoutesToMemoryTier` | < 50MB → MemoryStorageStrategy |
| `DiskFile_RoutesToChunkedDiskTier` | >= 50MB → ChunkedDiskStorageStrategy |
| `CutoffBoundary_ExactCutoff` | File exactly at `SmallFileCutoffMb` routes to disk (chunked) |
| `DiskTier_SmallFile_FewChunks` | 50MB file produces 5 chunks, all work correctly |
| `DiskTier_LargeFile_ManyChunks` | 5GB file produces 500 chunks, sequential extraction correct |

### 15.6 Endurance Tests

Extend the existing endurance test suite to include chunked-tier scenarios:
- Add files >= 200MB to the test fixture
- Add concurrent readers that access large files at random offsets
- Verify SHA-256 integrity of every read (including mid-extraction reads)
- Assert zero handle leaks (`BorrowedEntryCount == 0`) after test completion
- Assert extraction tasks all completed or were cleanly cancelled

---

## 16. Performance Analysis

### 16.1 First-Byte Latency Comparison

| File Size | Current (Extract-All) | Chunked (10MB chunks) | Improvement |
|-----------|----------------------|----------------------|-------------|
| 50 MB     | ~250ms               | ~50ms                | **5x**      |
| 100 MB    | ~500ms               | ~50ms                | **10x**     |
| 200 MB    | ~1.0s                | ~50ms                | **20x**     |
| 500 MB    | ~2.5s                | ~50ms                | **50x**     |
| 1 GB      | ~5.0s                | ~50ms                | **100x**    |
| 5 GB      | ~25.0s               | ~50ms                | **500x**    |

All disk-tier files (>= 50MB) benefit. Even at the cutoff boundary (50MB), first-byte latency improves 5x.

### 16.2 Throughput (Sequential Read)

Sequential read throughput is unchanged — both approaches eventually decompress the full file. The difference is when the first byte is available.

### 16.3 Overhead

| Overhead Source | Cost | When |
|-----------------|------|------|
| `TaskCompletionSource[]` allocation | ~80 bytes × ChunkCount | Once per entry creation |
| `BitArray` allocation | ChunkCount / 8 bytes | Once per entry creation |
| `ChunkedStream` per reader | ~100 bytes + FileStream | Per `Retrieve()` call |
| Per-chunk flush | ~0.1ms per chunk | During extraction |
| `EnsureChunkReadyAsync` check | < 100ns (bitmap check) | Per `Read()` call |

For a 5GB file with 10MB chunks (500 chunks):
- TCS array: 500 × 80 bytes = ~40KB
- BitArray: 500 / 8 = ~63 bytes
- Flush overhead: 500 × 0.1ms = ~50ms additional over full extraction

**Overhead is negligible** compared to the first-byte latency improvement.

### 16.4 Memory Footprint

The disk tier uses **no additional RAM** beyond the overhead above. All decompressed data goes to the NTFS sparse file. The OS manages page cache transparently via the `FileStream`. This is similar to the old `DiskStorageStrategy` (which used `MemoryMappedFile` → OS page cache).

---

## 17. Implementation Phases

### Phase 1: Core Types (Estimated: 1 day)

1. `ChunkedFileEntry` — chunk state tracking, TCS array, extraction lifecycle
2. `ChunkedStream` — Stream subclass with chunk-aware reads
3. Unit tests for both

### Phase 2: Storage Strategy (Estimated: 1 day)

1. `ChunkedDiskStorageStrategy` — `IStorageStrategy<Stream>` implementation
2. Sparse file creation and management
3. Background extraction task with clean error handling and cancellation
4. Unit tests for strategy

### Phase 3: Integration (Estimated: 1 day)

1. Add `ChunkSizeMb` to `CacheOptions`
2. Replace `DiskStorageStrategy` with `ChunkedDiskStorageStrategy` in `FileContentCache` constructor
3. Remove `DiskStorageStrategy` and `DiskCacheEntry` (replaced, no longer needed)
4. Update `FileContentCache.DeleteCacheDirectory` for new strategy
5. Wire DI in `ZipDrive.Cli` / `Program.cs`
6. Integration tests

### Phase 4: Telemetry + Observability (Estimated: 0.5 day)

1. Add chunked-specific counters and histograms to `CacheTelemetry`
2. Structured logging in extraction task and ChunkedStream
3. Verify Aspire Dashboard shows disk tier chunked extraction metrics

### Phase 5: Endurance Testing (Estimated: 1 day)

1. Extend endurance test fixture with large files (>= 200MB)
2. Add concurrent reader scenarios targeting the disk tier (chunked extraction)
3. SHA-256 integrity verification during extraction
4. Multi-hour soak test with tight capacity limits to force chunked eviction
5. Assert zero handle leaks and zero data corruption

---

## 18. Risks and Mitigations

### Risk 1: NTFS Sparse File Behavior

**Risk**: Sparse files behave differently from regular files. Reading unallocated regions returns zeros silently — no error, no exception.

**Mitigation**: The `EnsureChunkReadyAsync()` gate in `ChunkedStream` is the mandatory safety check. It MUST be called before every read. The `BitArray` and `TaskCompletionSource` double-check pattern ensures a reader never touches an unextracted region. Unit tests specifically verify that reads before chunk completion block correctly.

### Risk 2: Synchronous Dokan Callbacks

**Risk**: DokanNet's `ReadFile` callback may invoke `Stream.Read()` synchronously. Calling `Task.GetAwaiter().GetResult()` on a chunk wait could deadlock if the extraction task runs on the same thread pool.

**Mitigation**: The extraction task uses `FileOptions.Asynchronous`, which routes I/O to IOCP threads, not the thread pool. DokanNet uses its own thread pool. No contention. Additionally, the common case (chunk already ready) is a lock-free `BitArray` check with no async await at all.

### Risk 3: Capacity Accounting Accuracy

**Risk**: Reporting full `UncompressedSize` immediately may cause the cache to evict other entries prematurely, even though the sparse file hasn't consumed that space yet.

**Mitigation**: This is intentional conservative accounting. The alternative (reporting current extracted size with dynamic updates) would require changes to `GenericCache.CacheEntry.SizeBytes` (currently immutable via `StoredEntry.SizeBytes`). Conservative accounting is safe — it may under-utilize capacity slightly during extraction, but converges to accurate once extraction completes. Over-admission is the dangerous case, and this prevents it.

### Risk 4: Large Number of Concurrent Chunked Entries

**Risk**: If many files are accessed simultaneously, each starts a background extraction task. Too many concurrent extractions could saturate disk I/O.

**Mitigation**: The cache capacity limit naturally bounds the number of concurrent entries. With a 10GB disk tier, the number of concurrent extractions is bounded by capacity. Each extraction task does sequential I/O (efficient for HDDs and SSDs). Future optimization: an extraction semaphore to limit concurrent extractions (e.g., max 4 simultaneous).

### Risk 5: Regression for Existing Disk-Tier Behavior

**Risk**: Replacing the battle-tested `DiskStorageStrategy` (8-hour soak test validated) with new code could introduce regressions.

**Mitigation**: The `ChunkedDiskStorageStrategy` must pass all existing disk-tier tests (adapted to the new API). The endurance test suite is extended to cover chunked extraction under sustained load. The old `DiskStorageStrategy` code is preserved in git history if rollback is needed. The `IStorageStrategy<Stream>` interface guarantees behavioral compatibility — `GenericCache` treats both strategies identically.

---

## 19. Alternatives Considered

### Alternative 1: Add as Third Tier (Keep DiskStorageStrategy)

**Description**: Keep `DiskStorageStrategy` for files 50-200MB, add `ChunkedDiskStorageStrategy` as a third tier for files >= 200MB.

**Rejected because**:
- Adds unnecessary complexity (third `GenericCache<Stream>` instance, third routing branch, extra configuration parameter)
- 50MB files produce only 5 chunks — overhead is ~400 bytes of TCS objects, trivially negligible
- All disk-tier files benefit from first-byte latency improvement, not just the largest ones
- A 100MB file at 500ms first-byte latency is still noticeably slow — why exclude it?
- `DiskStorageStrategy` can be fully removed, reducing code surface area

### Alternative 2: Parallel System (ChunkedFileCache) Outside GenericCache

**Description**: Build a separate `ChunkedFileCache` that doesn't use `GenericCache<Stream>` at all.

**Rejected because**:
- Would duplicate thundering herd prevention (`Lazy<Task>`)
- Would duplicate TTL management
- Would duplicate eviction policy integration
- Would duplicate capacity tracking and RefCount protection
- Would duplicate telemetry integration
- Violates DRY — all of this machinery exists in `GenericCache` and works correctly

### Alternative 3: MemoryMappedFile Over Sparse File (Instead of FileStream)

**Description**: Create a `MemoryMappedFile` over the sparse file and use `CreateViewStream` for readers (matching `DiskStorageStrategy`).

**Rejected because**:
- `MemoryMappedFile` maps the entire file into virtual address space. Reading an unallocated sparse region returns zeros *without* going through our `EnsureChunkReadyAsync` gate — the MMF read succeeds silently.
- With `FileStream`, we control every `Read()` call and can interpose the chunk-ready check.
- `FileStream` with `FileShare.ReadWrite` provides the necessary concurrent read/write semantics.

### Alternative 4: One Temp File Per Chunk

**Description**: Create 500 separate temp files for a 5GB file (one per chunk).

**Rejected because**:
- 500 file handles per cached entry is excessive
- File system fragmentation
- More complex lifecycle management
- NTFS performance degrades with many small files in one directory
- Single sparse file with `FileShare.Read/ReadWrite` is simpler and faster
