## ADDED Requirements

### Requirement: Virtual Drive Mounting

The system SHALL mount a ZIP archive as a Windows virtual drive using DokanNet.

#### Scenario: Mount single ZIP archive
- **WHEN** user provides a valid ZIP file path and mount path (drive letter)
- **THEN** the system creates a DokanNet instance with the specified drive letter
- **AND** the drive appears in Windows Explorer as a read-only fixed drive
- **AND** the ZIP archive's root directory is accessible at the mount path

#### Scenario: Mount with invalid ZIP path
- **WHEN** user provides a non-existent or invalid ZIP file path
- **THEN** the system throws an appropriate exception with descriptive message
- **AND** no drive is mounted

#### Scenario: Mount with occupied drive letter
- **WHEN** user provides a drive letter already in use
- **THEN** the system throws an appropriate exception
- **AND** no partial mount state is created

---

### Requirement: Virtual Drive Unmounting

The system SHALL cleanly unmount the virtual drive and release all resources.

#### Scenario: Clean unmount
- **WHEN** the unmount operation is requested
- **THEN** the system calls Dokan.RemoveMountPoint()
- **AND** disposes the archive session
- **AND** the drive letter becomes available again

#### Scenario: Unmount with open file handles
- **WHEN** unmount is requested while files are open
- **THEN** the system waits for DokanNet to complete pending operations
- **AND** eventually unmounts (DokanNet handles force close)

---

### Requirement: File Reading

The system SHALL support reading file contents at arbitrary offsets via DokanNet ReadFile callback.

#### Scenario: Read file from cache hit
- **WHEN** ReadFile is called for a previously accessed file
- **THEN** the system borrows the cached content via IFileCache
- **AND** seeks to the requested offset
- **AND** reads the requested number of bytes
- **AND** disposes the cache handle after read
- **AND** returns DokanResult.Success

#### Scenario: Read file from cache miss
- **WHEN** ReadFile is called for a file not in cache
- **THEN** the system extracts the file via IZipReader.OpenEntryStreamAsync()
- **AND** materializes the content to cache (memory or disk based on size)
- **AND** returns the requested bytes
- **AND** subsequent reads use the cached content

#### Scenario: Read non-existent file
- **WHEN** ReadFile is called for a path that doesn't exist in the archive
- **THEN** the system returns DokanResult.FileNotFound

#### Scenario: Concurrent reads of same file
- **WHEN** multiple threads call ReadFile for the same file simultaneously
- **THEN** only one materialization occurs (thundering herd prevention)
- **AND** all threads receive correct data
- **AND** cache RefCount prevents eviction during reads

---

### Requirement: Directory Listing

The system SHALL support listing directory contents via DokanNet FindFilesWithPattern callback.

#### Scenario: List root directory
- **WHEN** FindFilesWithPattern is called for the root path "\"
- **THEN** the system returns all top-level files and directories from the archive
- **AND** each entry includes correct FileInformation (name, size, attributes, timestamps)

#### Scenario: List subdirectory
- **WHEN** FindFilesWithPattern is called for a subdirectory path
- **THEN** the system returns direct children of that directory
- **AND** does not include nested subdirectory contents

#### Scenario: List non-existent directory
- **WHEN** FindFilesWithPattern is called for a path that doesn't exist
- **THEN** the system returns DokanResult.PathNotFound

---

### Requirement: File Information

The system SHALL provide file metadata via DokanNet GetFileInformation callback.

#### Scenario: Get file information
- **WHEN** GetFileInformation is called for a file path
- **THEN** the system returns FileInformation populated from ZipEntryInfo
- **AND** includes: FileName, Length, Attributes (ReadOnly), CreationTime, LastWriteTime

#### Scenario: Get directory information
- **WHEN** GetFileInformation is called for a directory path
- **THEN** the system returns FileInformation with Directory attribute
- **AND** Length is 0

---

### Requirement: Volume Information

The system SHALL provide volume metadata via DokanNet GetVolumeInformation callback.

#### Scenario: Get volume information
- **WHEN** GetVolumeInformation is called
- **THEN** the system returns volume label (archive filename without extension)
- **AND** returns file system name "ZipDriveFS"
- **AND** returns read-only file system features flag

---

### Requirement: CreateFile Validation

The system SHALL validate file paths and return appropriate access status via DokanNet CreateFile callback.

#### Scenario: Open existing file for read
- **WHEN** CreateFile is called for an existing file with read access
- **THEN** the system returns DokanResult.Success

#### Scenario: Open existing directory
- **WHEN** CreateFile is called for an existing directory
- **THEN** the system returns DokanResult.Success

#### Scenario: Open non-existent path
- **WHEN** CreateFile is called for a path that doesn't exist
- **THEN** the system returns DokanResult.FileNotFound or DokanResult.PathNotFound

#### Scenario: Attempt write access
- **WHEN** CreateFile is called with write or create disposition
- **THEN** the system returns DokanResult.AccessDenied (read-only mount)

---

### Requirement: Write Operations Rejected

The system SHALL reject all write operations as the mount is read-only.

#### Scenario: Write file rejected
- **WHEN** WriteFile is called
- **THEN** the system returns DokanResult.AccessDenied

#### Scenario: Delete file rejected
- **WHEN** DeleteFile is called
- **THEN** the system returns DokanResult.AccessDenied

#### Scenario: Create directory rejected
- **WHEN** CreateFile is called with create disposition for directory
- **THEN** the system returns DokanResult.AccessDenied

---

### Requirement: Archive Session Management

The system SHALL manage the archive session lifecycle for the duration of the mount.

#### Scenario: Session created on mount
- **WHEN** mount operation begins
- **THEN** the system creates ZipArchiveSession via IArchiveProvider.OpenAsync()
- **AND** the session parses the ZIP Central Directory
- **AND** builds the ArchiveStructure with file tree

#### Scenario: Session disposed on unmount
- **WHEN** unmount operation completes
- **THEN** the system calls IArchiveSession.DisposeAsync()
- **AND** releases the IZipReader resources
- **AND** invalidates related cache entries (optional)

---

### Requirement: Disk Space Information

The system SHALL report disk space information via DokanNet GetDiskFreeSpace callback.

#### Scenario: Get disk free space
- **WHEN** GetDiskFreeSpace is called
- **THEN** the system returns total bytes as sum of uncompressed file sizes
- **AND** returns free bytes as 0 (read-only)
- **AND** returns available bytes as 0
