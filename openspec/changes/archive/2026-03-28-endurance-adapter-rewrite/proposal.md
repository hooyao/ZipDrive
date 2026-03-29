## Why

All endurance tests currently call `IVirtualFileSystem` directly, bypassing `DokanFileSystemAdapter`. This misses the adapter's error translation, buffer pooling, shell metadata filtering, and any future adapter-level logic. The user requirement is explicit: endurance tests MUST go through `DokanFileSystemAdapter` using `Guarded*Async` methods, testing realistic multi-step user operation sequences — not isolated VFS calls.

## What Changes

- Add 5 `Guarded*Async` public methods to `DokanFileSystemAdapter` (test-facing async API, no Dokan native types)
- Change `EnduranceSuiteBase` from `IVirtualFileSystem` to `DokanFileSystemAdapter`
- Update all 8 existing endurance suites to use adapter calls
- Update `EnduranceTest.cs` fixture to build and wire the adapter
- Rewrite `DynamicReloadSuite` with 10 categories of logical use-case scenarios through the adapter
- Add comprehensive post-run assertions (trie-disk consistency, temp file cleanup, handle leak detection)

## Capabilities

### New Capabilities
- `endurance-adapter-api`: Guarded*Async methods on DokanFileSystemAdapter for test consumption without Dokany runtime

### Modified Capabilities
- `endurance-testing`: Rewrite all suites to use adapter, add 10-category DynamicReloadSuite with ~90 concurrent logical scenario tasks

## Impact

- **DokanFileSystemAdapter**: 5 new public methods (GuardedReadFileAsync, GuardedListDirectoryAsync, GuardedGetFileInfoAsync, GuardedFileExistsAsync, GuardedDirectoryExistsAsync)
- **EnduranceSuiteBase**: Constructor changes from `IVirtualFileSystem` to `DokanFileSystemAdapter`
- **All 8 endurance suites**: Constructor + all VFS call sites updated
- **EnduranceTest.cs**: Builds adapter instance, passes to suites
- **DynamicReloadSuite**: Complete rewrite with logical scenario tasks
- **No production behavior changes**: Guarded methods simply delegate to VFS
