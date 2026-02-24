## ADDED Requirements

### Requirement: Implement IDokanOperations2

The `DokanFileSystemAdapter` SHALL implement `IDokanOperations2` and delegate all file system operations to `IVirtualFileSystem`.

#### Scenario: ReadFile delegates to VFS

- **WHEN** DokanNet calls `ReadFile` with a path, buffer, and offset
- **THEN** the adapter converts `ReadOnlyNativeMemory<char>` to string, calls `VFS.ReadFileAsync`, writes the result into `NativeMemory<byte>.Span`, and sets `bytesRead`
- **AND** returns `DokanResult.Success`

#### Scenario: FindFilesWithPattern delegates to VFS

- **WHEN** DokanNet calls `FindFilesWithPattern` with a directory path and search pattern
- **THEN** the adapter calls `VFS.ListDirectoryAsync`, converts each `VfsFileInfo` to `FindFileInformation`, filters by pattern using `DokanHelper.DokanIsNameInExpression`, and returns the collection

#### Scenario: GetFileInformation delegates to VFS

- **WHEN** DokanNet calls `GetFileInformation` with a file path
- **THEN** the adapter calls `VFS.GetFileInfoAsync` and converts the result to `ByHandleFileInformation`

#### Scenario: CreateFile validates existence for read-only access

- **WHEN** DokanNet calls `CreateFile` with `FileMode.Open`
- **THEN** the adapter checks if the path exists via VFS, sets `info.IsDirectory` appropriately, and returns `DokanResult.Success`

#### Scenario: CreateFile rejects write modes

- **WHEN** DokanNet calls `CreateFile` with `FileMode.CreateNew`, `FileMode.Create`, or `FileMode.Append`
- **THEN** the adapter returns `DokanResult.AccessDenied`

---

### Requirement: Read-only enforcement

All write operations SHALL return `DokanResult.AccessDenied`. This includes `WriteFile`, `DeleteFile`, `DeleteDirectory`, `MoveFile`, `SetFileAttributes`, `SetFileTime`, `SetEndOfFile`, `SetAllocationSize`, and `SetFileSecurity`.

#### Scenario: Write operations rejected

- **WHEN** any write operation is called
- **THEN** `DokanResult.AccessDenied` is returned without invoking VFS

---

### Requirement: Volume information

`GetVolumeInformation` SHALL report the file system as "ZipDriveFS" with `WriteProtection` and case-preserving features. `GetDiskFreeSpace` SHALL report 0 free bytes.

#### Scenario: Volume info returned

- **WHEN** `GetVolumeInformation` is called
- **THEN** volume label is set to "ZipDrive", file system name to "ZipDriveFS", and features include `CasePreservedNames` and `UnicodeOnDisk`

---

### Requirement: Exception-to-NtStatus mapping

VFS exceptions SHALL be caught and mapped to appropriate DokanNet status codes.

#### Scenario: VfsFileNotFoundException maps to FileNotFound

- **WHEN** the VFS throws `VfsFileNotFoundException`
- **THEN** the adapter returns `DokanResult.FileNotFound`

#### Scenario: VfsDirectoryNotFoundException maps to PathNotFound

- **WHEN** the VFS throws `VfsDirectoryNotFoundException`
- **THEN** the adapter returns `DokanResult.PathNotFound`

#### Scenario: VfsAccessDeniedException maps to AccessDenied

- **WHEN** the VFS throws `VfsAccessDeniedException`
- **THEN** the adapter returns `DokanResult.AccessDenied`

#### Scenario: Unexpected exceptions map to InternalError

- **WHEN** the VFS throws an unexpected exception
- **THEN** the adapter logs the error and returns `DokanResult.InternalError`
