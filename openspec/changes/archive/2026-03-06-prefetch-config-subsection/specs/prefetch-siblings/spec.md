## MODIFIED Requirements

### Requirement: Prefetch Configuration
All prefetch behavior SHALL be configurable via `PrefetchOptions` bound from the `Cache:Prefetch` configuration subsection, with defaults that are safe for production use.

#### Scenario: Prefetch disabled by config
- **WHEN** `Cache:Prefetch:Enabled` is set to `false`
- **THEN** no prefetch is triggered on any `ReadFileAsync` or `FindFilesAsync` call

#### Scenario: File size threshold filters large files
- **WHEN** a sibling file's uncompressed size exceeds `FileSizeThresholdMb`
- **THEN** that sibling is excluded from prefetch candidates

#### Scenario: Default configuration is safe
- **WHEN** no prefetch config is specified
- **THEN** defaults are: `Enabled=false`, `OnRead=true`, `OnListDirectory=false`, `FileSizeThresholdMb=10`, `MaxFiles=20`, `MaxDirectoryFiles=300`, `FillRatioThreshold=0.80`

#### Scenario: Enabling prefetch activates read-triggered prefetch immediately
- **WHEN** only `Cache:Prefetch:Enabled` is set to `true` (no other overrides)
- **THEN** read-triggered prefetch is active (because `OnRead` defaults to `true`)
- **AND** list-triggered prefetch remains inactive (because `OnListDirectory` defaults to `false`)
