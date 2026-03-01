## 1. Implementation

- [x] 1.1 Replace `new byte[]` with `RentArray()`/`ReturnArray()` in `DokanFileSystemAdapter.ReadFile()`, ensuring buffer is returned in all code paths (success, EOF, exception)

## 2. Verification

- [x] 2.1 Build succeeds and all existing tests pass
