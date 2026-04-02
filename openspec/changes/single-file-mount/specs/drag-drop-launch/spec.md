## MODIFIED Requirements

### Requirement: Bare positional argument rewriting

The application SHALL detect a bare positional argument (first argument not starting with `--`) and rewrite it to `--Mount:ArchiveDirectory=<value>` before passing args to the host builder. This applies to both file paths and directory paths — `ArgPreprocessor` does not distinguish between them.

#### Scenario: Folder dragged onto exe

- **WHEN** the application is launched with args `["D:\my-zips"]`
- **THEN** the args passed to `Host.CreateDefaultBuilder` SHALL include `--Mount:ArchiveDirectory=D:\my-zips`
- **AND** `Mount:ArchiveDirectory` SHALL resolve to `D:\my-zips`

#### Scenario: Single file dragged onto exe

- **WHEN** the application is launched with args `["D:\Downloads\game.zip"]`
- **THEN** the args passed to `Host.CreateDefaultBuilder` SHALL include `--Mount:ArchiveDirectory=D:\Downloads\game.zip`
- **AND** `Mount:ArchiveDirectory` SHALL resolve to `D:\Downloads\game.zip`

#### Scenario: Bare arg with explicit override

- **WHEN** the application is launched with args `["D:\my-zips", "--Mount:ArchiveDirectory=E:\other"]`
- **THEN** `Mount:ArchiveDirectory` SHALL resolve to `E:\other` (explicit named arg wins via last-wins semantics)

#### Scenario: No bare arg

- **WHEN** the application is launched with args `["--Mount:ArchiveDirectory=D:\my-zips"]`
- **THEN** args SHALL be passed through unchanged

#### Scenario: No args at all

- **WHEN** the application is launched with no arguments
- **THEN** args SHALL be passed through unchanged (no rewriting occurs)
