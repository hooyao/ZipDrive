## ADDED Requirements

### Requirement: Centralized version management

The project SHALL have a `Directory.Build.props` file at the repository root that defines version properties inherited by all projects.

#### Scenario: Default development version

- **WHEN** the solution is built without a version override
- **THEN** all assemblies SHALL have version `1.0.0-dev`

#### Scenario: Version override at publish time

- **WHEN** the solution is published with `-p:Version=1.0.1`
- **THEN** all assemblies SHALL have `AssemblyInformationalVersion` set to `1.0.1`
- **AND** `AssemblyVersion` SHALL be set to `1.0.0` (major.minor.0 for assembly binding stability)
- **AND** `FileVersion` SHALL be set to `1.0.1.0`

#### Scenario: Prerelease version override

- **WHEN** the solution is published with `-p:Version=1.0.1-beta`
- **THEN** `AssemblyInformationalVersion` SHALL be `1.0.1-beta`

---

### Requirement: Startup version logging

The CLI application SHALL log the product version at startup before any other application logic.

#### Scenario: Version logged at startup

- **WHEN** the application starts
- **THEN** it SHALL log a message containing `"ZipDrive"` and the `AssemblyInformationalVersion` value at Information level
- **AND** the version SHALL be read from `AssemblyInformationalVersionAttribute` on the entry assembly

#### Scenario: Development build shows dev version

- **WHEN** the application is started from a local build (no version override)
- **THEN** the startup log SHALL show version `1.0.0-dev`

---

### Requirement: CLI executable output name

The CLI project SHALL produce an executable named `ZipDrive.exe` instead of `ZipDrive.Cli.exe`.

#### Scenario: Published executable name

- **WHEN** the CLI project is published as a single-file executable
- **THEN** the output file SHALL be named `ZipDrive.exe`

#### Scenario: AssemblyName set in CLI project

- **WHEN** the CLI project's `.csproj` is inspected
- **THEN** it SHALL contain `<AssemblyName>ZipDrive</AssemblyName>`
