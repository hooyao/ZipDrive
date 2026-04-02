## MODIFIED Requirements

### Requirement: Discover ZIP files in a directory tree
The system SHALL discover all supported archive formats (not just ZIP) in a directory tree. It SHALL iterate `IFormatRegistry.SupportedExtensions` and enumerate files matching each extension pattern. Each discovered file SHALL have its format detected via `IFormatRegistry.DetectFormat(filePath)`.

#### Scenario: Discover ZIPs at root level (depth=1)
- **WHEN** root directory contains `a.zip` and `b.zip`
- **THEN** both are returned as `ArchiveDescriptor` with `FormatId == "zip"`

#### Scenario: Discover RARs at root level
- **WHEN** root directory contains `a.rar` and `b.rar`
- **THEN** both are returned as `ArchiveDescriptor` with `FormatId == "rar"`

#### Scenario: Discover mixed formats
- **WHEN** root directory contains `a.zip`, `b.rar`, and `c.txt`
- **THEN** `a.zip` (FormatId=zip) and `b.rar` (FormatId=rar) are returned
- **AND** `c.txt` is not returned

#### Scenario: Discover ZIPs in subdirectories (depth=2)
- **WHEN** root directory contains `sub/a.zip` and `sub/b.rar`
- **THEN** both are returned with correct format IDs and virtual paths preserving directory structure

#### Scenario: Depth limit is respected
- **WHEN** max depth is 1 and `sub/deep/a.rar` exists at depth 2
- **THEN** `a.rar` is NOT returned

#### Scenario: Empty directory returns empty list
- **WHEN** root directory has no supported archive files
- **THEN** an empty list is returned

### Requirement: ArchiveDescriptor metadata
`ArchiveDescriptor` SHALL include `FormatId` (string, required) in addition to existing fields. The `FormatId` SHALL be set by `IFormatRegistry.DetectFormat(filePath)` during discovery.

#### Scenario: Descriptor contains correct metadata
- **WHEN** a `.rar` file is discovered
- **THEN** its `ArchiveDescriptor` has `FormatId == "rar"`, correct `VirtualPath`, `PhysicalPath`, `SizeBytes`, and `LastModifiedUtc`

## ADDED Requirements

### Requirement: DescribeFile sets FormatId for dynamic reload
`ArchiveDiscovery.DescribeFile(rootPath, filePath)` SHALL call `IFormatRegistry.DetectFormat(filePath)` and return null if the format is unrecognized. The returned `ArchiveDescriptor` SHALL include the detected `FormatId`.

#### Scenario: DescribeFile for RAR file
- **WHEN** `DescribeFile` is called with a `.rar` file
- **THEN** the returned descriptor has `FormatId == "rar"`

#### Scenario: DescribeFile for unsupported extension
- **WHEN** `DescribeFile` is called with a `.7z` file (no provider registered)
- **THEN** null is returned
