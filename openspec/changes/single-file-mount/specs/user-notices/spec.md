## ADDED Requirements

### Requirement: UserNotice static helper
A static `UserNotice` class SHALL provide methods to display visually prominent notices using Spectre.Console `Panel` widgets. Methods: `Tip(string title, string body)`, `Warning(string title, string body)`, `Error(string title, string body)`.

#### Scenario: Tip notice rendering
- **WHEN** `UserNotice.Tip("TIP", "Some message")` is called
- **THEN** a Spectre.Console `Panel` SHALL be written to `AnsiConsole` with `RoundedBorder()`, blue border color, and header `[blue bold] TIP [/]`

#### Scenario: Warning notice rendering
- **WHEN** `UserNotice.Warning("WARNING", "Some message")` is called
- **THEN** a Spectre.Console `Panel` SHALL be written to `AnsiConsole` with `RoundedBorder()`, yellow border color, and header `[yellow bold] WARNING [/]`

#### Scenario: Error notice rendering
- **WHEN** `UserNotice.Error("ERROR", "Some message")` is called
- **THEN** a Spectre.Console `Panel` SHALL be written to `AnsiConsole` with `DoubleBorder()`, red border color, and header `[red bold] ERROR [/]`

### Requirement: Spectre.Console dependency
The `Spectre.Console` NuGet package SHALL be added to `Directory.Packages.props` and referenced by the project containing `DokanHostedService` (`ZipDrive.Infrastructure.FileSystem`).

#### Scenario: Package registration
- **WHEN** the solution is built
- **THEN** `Spectre.Console` SHALL be resolvable via Central Package Management in `Directory.Packages.props`

### Requirement: Single-file mount tip
When a single archive file is successfully mounted, the system SHALL display a TIP notice informing the user they can drag a folder to mount multiple archives.

#### Scenario: Single file mount tip displayed
- **WHEN** a single archive file is successfully mounted
- **THEN** a TIP panel SHALL be displayed with the message: "You mounted a single archive file.\nDrag a FOLDER onto ZipDrive.exe to mount all archives inside it at once!"

### Requirement: Empty directory warning
When a directory is mounted but contains zero supported archives, the system SHALL display a WARNING notice.

#### Scenario: Empty directory warning displayed
- **WHEN** `DiscoverAsync` returns zero archives for the given directory
- **THEN** a WARNING panel SHALL be displayed with the message: "No supported archives found in \"{dirName}\"\nSupported formats: {extensions from IFormatRegistry}\nThe drive is mounted but empty. Add ZIP or RAR files and they will appear automatically."

### Requirement: Unsupported file error
When a single file is dragged but its format is not recognized, the system SHALL display an ERROR notice with the unsupported extension and all supported formats.

#### Scenario: Unsupported file error displayed
- **WHEN** the user provides a file path whose extension is not recognized by `IFormatRegistry.DetectFormat()`
- **THEN** an ERROR panel SHALL be displayed with: "Cannot mount \"{filename}\"\nFile type \"{ext}\" is not a supported archive format.\nSupported formats: {list}\n\nTip: Drag a ZIP or RAR file, or a folder containing them."
- **AND** the system SHALL wait for a key press before exiting

### Requirement: Path empty error
When `Mount:ArchiveDirectory` is empty, the system SHALL display an ERROR notice with guidance.

#### Scenario: Empty path error displayed
- **WHEN** `Mount:ArchiveDirectory` is null or whitespace
- **THEN** an ERROR panel SHALL be displayed with: "No archive path specified.\nDrag a ZIP/RAR file or a folder onto ZipDrive.exe,\nor set Mount:ArchiveDirectory in appsettings.jsonc."
- **AND** the system SHALL wait for a key press before exiting

### Requirement: Path not found error
When the provided path does not exist as either a file or directory, the system SHALL display an ERROR notice.

#### Scenario: Non-existent path error displayed
- **WHEN** `Mount:ArchiveDirectory` points to a path that is neither a file nor a directory
- **THEN** an ERROR panel SHALL be displayed with: "Path not found: {path}"
- **AND** the system SHALL wait for a key press before exiting
