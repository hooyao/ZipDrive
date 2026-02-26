## MODIFIED Requirements

### Requirement: Read file content

`ReadFileAsync` SHALL read decompressed file content from an archive entry at a specified byte offset into a provided buffer. It SHALL delegate to `IFileContentCache.ReadAsync` for extraction, caching, and byte retrieval. It SHALL NOT construct factory delegates, create `IZipReader` instances, or reference caching configuration.

#### Scenario: Read entire small file

- **WHEN** `ReadFileAsync("archive.zip/readme.txt", buffer, offset: 0)` is called
- **AND** the file is 500 bytes
- **AND** buffer size is 4096
- **THEN** 500 bytes are written to the buffer
- **AND** the return value is 500

#### Scenario: Read file at offset

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 1000)` is called
- **AND** the file is 5000 bytes
- **AND** buffer size is 2000
- **THEN** 2000 bytes are written to the buffer starting from offset 1000
- **AND** the return value is 2000

#### Scenario: Read past end of file

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 4500)` is called
- **AND** the file is 5000 bytes
- **AND** buffer size is 2000
- **THEN** 500 bytes are written to the buffer (remaining bytes from offset to EOF)
- **AND** the return value is 500

#### Scenario: Read at or beyond EOF

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 5000)` is called
- **AND** the file is 5000 bytes
- **THEN** the return value is 0

#### Scenario: Cache hit for repeated reads

- **WHEN** the same file is read multiple times
- **THEN** the file content cache returns the cached stream on subsequent reads
- **AND** the ZIP is NOT re-parsed

#### Scenario: Read from a directory path

- **WHEN** `ReadFileAsync("archive.zip/maps", buffer, offset: 0)` is called
- **AND** `maps` is a directory
- **THEN** a `VfsAccessDeniedException` is thrown

#### Scenario: Read from non-existent file

- **WHEN** `ReadFileAsync("archive.zip/nonexistent.txt", buffer, offset: 0)` is called
- **THEN** a `VfsFileNotFoundException` is thrown

#### Scenario: ZipVirtualFileSystem does not depend on extraction infrastructure

- **WHEN** `ZipVirtualFileSystem` is constructed
- **THEN** it SHALL NOT receive `IZipReaderFactory`, `CacheOptions`, or `DualTierFileCache` as dependencies
- **AND** it SHALL receive `IFileContentCache` as the only cache dependency
