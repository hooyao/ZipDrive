## ADDED Requirements

### Requirement: DokanFileSystemAdapter exposes Guarded*Async methods
DokanFileSystemAdapter SHALL expose 5 public async methods that delegate to the underlying VFS. These methods accept normal string paths and byte[] buffers (no Dokan native memory types), enabling test consumption without Dokany installed.

#### Scenario: GuardedReadFileAsync delegates to VFS
- **WHEN** `GuardedReadFileAsync(path, buffer, offset, ct)` is called
- **THEN** it returns the result of `_vfs.ReadFileAsync(path, buffer, offset, ct)`

#### Scenario: GuardedListDirectoryAsync delegates to VFS
- **WHEN** `GuardedListDirectoryAsync(path, ct)` is called
- **THEN** it returns the result of `_vfs.ListDirectoryAsync(path, ct)`

#### Scenario: GuardedGetFileInfoAsync delegates to VFS
- **WHEN** `GuardedGetFileInfoAsync(path, ct)` is called
- **THEN** it returns the result of `_vfs.GetFileInfoAsync(path, ct)`

#### Scenario: GuardedFileExistsAsync delegates to VFS
- **WHEN** `GuardedFileExistsAsync(path, ct)` is called
- **THEN** it returns the result of `_vfs.FileExistsAsync(path, ct)`

#### Scenario: GuardedDirectoryExistsAsync delegates to VFS
- **WHEN** `GuardedDirectoryExistsAsync(path, ct)` is called
- **THEN** it returns the result of `_vfs.DirectoryExistsAsync(path, ct)`
