## Context

`DokanFileSystemAdapter.ReadFile()` currently allocates `new byte[buffer.Span.Length]` (typically 64KB) on every read, then copies data from the VFS stream into it, then copies again into Dokan's native buffer. DokanNet's `NativeMemory<T>` provides `RentArray()` which borrows from `ArrayPool<T>.Shared`, and `ReturnArray(array, copyBack)` which copies the contents back to native memory and returns the array to the pool.

## Goals / Non-Goals

**Goals:** Eliminate per-read `byte[]` allocation by using `NativeMemory<T>.RentArray()`/`ReturnArray()`.

**Non-Goals:** Changing the VFS API signature, zero-copy reads, or reducing Dokany's own unmanaged memory footprint (~394MB baseline from the kernel driver).

## Decisions

### Use ArrayPool directly (not RentArray/ReturnArray)

**Decision**: Use `ArrayPool<byte>.Shared.Rent()` and `.Return()` directly, with a manual bounded copy of only `bytesRead` bytes to the native buffer.

**Rationale**: `NativeMemory<T>.ReturnArray()` always copies the entire rented buffer back to native memory — it has no byte-count parameter. Since `ArrayPool` arrays are not zeroed, this would leak stale pool data into the Dokan native buffer beyond `bytesRead`. Using `ArrayPool` directly with `rentedArray.AsSpan(0, bytesRead).CopyTo(buffer.Span)` copies only valid bytes.

**Note**: `GetStream()` or `GetMemoryManager()` would require changing `IVirtualFileSystem.ReadFileAsync` to accept `Stream` or `Memory<byte>` targets, which is unnecessary complexity for this optimization.

## Risks / Trade-offs

- **[Risk] Rented array may be larger than requested**: `ArrayPool` may return a larger array. The VFS reads up to `rentedArray.Length` bytes (potentially more than the native buffer), but `bytesRead` is capped to `requestedLength` and only that many bytes are copied. Extra decompression work is wasted but harmless.
- **[Trade-off] Still one copy** (pool → native): True zero-copy would require `Memory<byte>` throughout the stack. Not worth the API churn for a 64KB buffer.
