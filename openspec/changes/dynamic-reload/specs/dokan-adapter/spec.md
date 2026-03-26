## MODIFIED Requirements

### Requirement: Implement IDokanOperations2
DokanFileSystemAdapter SHALL implement the DokanNet `IDokanOperations2` interface, translating each callback to the corresponding `IVirtualFileSystem` method. The VFS reference SHALL be volatile and accessed via local snapshot in each callback. Each callback SHALL participate in the adapter drain reference counting protocol (increment on entry, decrement in finally block). When drain mode is active, callbacks SHALL return `NtStatus.DeviceBusy`.

#### Scenario: ReadFile delegates to VFS with pooled buffer
- **WHEN** Dokan calls ReadFile
- **THEN** adapter increments active count, captures VFS snapshot, rents a buffer from ArrayPool, delegates to `vfs.ReadFileAsync`, copies bytes to native buffer, returns pool buffer in finally, and decrements active count

#### Scenario: ReadFile returns zero bytes
- **WHEN** `vfs.ReadFileAsync` returns 0 bytes
- **THEN** `bytesRead` SHALL be 0 and result SHALL be `Success`

#### Scenario: ReadFile exception returns buffer to pool
- **WHEN** `vfs.ReadFileAsync` throws an exception
- **THEN** the rented buffer SHALL still be returned to the pool via finally block and active count SHALL be decremented

#### Scenario: FindFilesWithPattern delegates to VFS
- **WHEN** Dokan calls FindFilesWithPattern
- **THEN** adapter increments active count, captures VFS snapshot, delegates to `vfs.ListDirectoryAsync`, filters by pattern, and decrements active count

#### Scenario: GetFileInformation delegates to VFS
- **WHEN** Dokan calls GetFileInformation
- **THEN** adapter increments active count, captures VFS snapshot, delegates to `vfs.GetFileInfoAsync`, and decrements active count

#### Scenario: CreateFile validates existence for read-only access
- **WHEN** Dokan calls CreateFile with OpenExisting mode
- **THEN** adapter increments active count, captures VFS snapshot, checks existence via VFS, and decrements active count

#### Scenario: CreateFile rejects write modes
- **WHEN** Dokan calls CreateFile with CreateNew, Create, or Append mode
- **THEN** adapter returns `AccessDenied` without incrementing active count

#### Scenario: Write operations rejected
- **WHEN** Dokan calls any write operation (WriteFile, DeleteFile, MoveFile, etc.)
- **THEN** adapter returns `AccessDenied` without incrementing active count

#### Scenario: Drain mode returns DeviceBusy
- **WHEN** drain mode is active and any read callback is invoked
- **THEN** adapter returns `NtStatus.DeviceBusy` without accessing the VFS

#### Scenario: Volume info returned
- **WHEN** Dokan calls GetVolumeInformation
- **THEN** adapter returns volume label "ZipDrive" and filesystem name "ZipDriveFS"

#### Scenario: VfsFileNotFoundException maps to FileNotFound
- **WHEN** VFS throws VfsFileNotFoundException
- **THEN** adapter returns `DokanResult.FileNotFound`

#### Scenario: VfsDirectoryNotFoundException maps to PathNotFound
- **WHEN** VFS throws VfsDirectoryNotFoundException
- **THEN** adapter returns `DokanResult.PathNotFound`

#### Scenario: VfsAccessDeniedException maps to AccessDenied
- **WHEN** VFS throws VfsAccessDeniedException
- **THEN** adapter returns `DokanResult.AccessDenied`

#### Scenario: Unexpected exceptions map to InternalError
- **WHEN** VFS throws an unexpected exception
- **THEN** adapter returns `DokanResult.InternalError`
