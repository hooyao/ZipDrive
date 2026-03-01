## Why

`DokanFileSystemAdapter.ReadFile()` allocates a fresh `byte[]` on every read call to bridge between the VFS API (`byte[]`) and Dokan's native buffer (`NativeMemory<byte>`). Under sustained I/O (Explorer browsing, antivirus scanning), this creates GC pressure from thousands of short-lived 64KB allocations per second. DokanNet's `NativeMemory<T>` already provides `RentArray()`/`ReturnArray()` for pooled buffer access — we should use it.

## What Changes

- Replace `new byte[buffer.Span.Length]` with `buffer.RentArray()` (borrows from ArrayPool)
- Replace manual `CopyTo(buffer.Span)` with `buffer.ReturnArray(array, copyBack: true)` (copies back + returns to pool)
- 3-line change in `DokanFileSystemAdapter.ReadFile()`

## Capabilities

### New Capabilities

_(none — this is a micro-optimization within an existing component)_

### Modified Capabilities

- `dokan-adapter`: ReadFile buffer allocation changed from `new byte[]` to pooled via `NativeMemory<T>.RentArray()`

## Impact

- **Code**: `src/ZipDrive.Infrastructure.FileSystem/DokanFileSystemAdapter.cs` — ReadFile method only
- **No API changes** — `IVirtualFileSystem` interface unchanged
- **No behavioral changes** — identical read semantics, just pooled allocation
- **Risk**: Minimal — `RentArray`/`ReturnArray` is the DokanNet-recommended pattern
