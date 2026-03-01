## Context

`DokanFileSystemAdapter.ReadFile()` currently allocates `new byte[buffer.Span.Length]` (typically 64KB) on every read, then copies data from the VFS stream into it, then copies again into Dokan's native buffer. DokanNet's `NativeMemory<T>` provides `RentArray()` which borrows from `ArrayPool<T>.Shared`, and `ReturnArray(array, copyBack)` which copies the contents back to native memory and returns the array to the pool.

## Goals / Non-Goals

**Goals:** Eliminate per-read `byte[]` allocation by using `NativeMemory<T>.RentArray()`/`ReturnArray()`.

**Non-Goals:** Changing the VFS API signature, zero-copy reads, or reducing Dokany's own unmanaged memory footprint (~394MB baseline from the kernel driver).

## Decisions

### Use RentArray/ReturnArray (not GetStream or GetMemoryManager)

**Decision**: Use `buffer.RentArray()` and `buffer.ReturnArray(rentedBuffer, read > 0)`.

**Rationale**: Drop-in replacement — no API changes, no new abstractions, 3-line diff. `GetStream()` or `GetMemoryManager()` would require changing `IVirtualFileSystem.ReadFileAsync` to accept `Stream` or `Memory<byte>` targets, which is unnecessary complexity for this optimization.

**Note**: `ReturnArray` with `copyBack: false` when `read == 0` avoids an unnecessary copy on EOF/error.

## Risks / Trade-offs

- **[Risk] Rented array may be larger than requested**: `ArrayPool` may return a larger array. `ReadFileAsync` uses `buffer.Length` to determine bytes to read, so the extra capacity is harmless — only `read` bytes are copied back via `ReturnArray`.
- **[Trade-off] Still one copy** (pool → native): True zero-copy would require `Memory<byte>` throughout the stack. Not worth the API churn for a 64KB buffer.
