## ADDED Requirements

### Requirement: Tag-driven release workflow

The project SHALL have a GitHub Actions workflow at `.github/workflows/release.yml` that triggers on tags matching `release-*` and produces a GitHub Release with versioned assets.

#### Scenario: Tag triggers release build

- **WHEN** a tag matching `release-*` is pushed (e.g., `release-1.0.1`)
- **THEN** the workflow SHALL parse the version by stripping the `release-` prefix (yielding `1.0.1`)
- **AND** the workflow SHALL restore, build, and test the solution in Release configuration
- **AND** the workflow SHALL publish a single-file win-x64 executable with `-p:Version=<parsed-version>`
- **AND** the workflow SHALL create a GitHub Release titled `ZipDrive <version>` with the published executable and `appsettings.json` as assets

#### Scenario: Prerelease tag creates prerelease release

- **WHEN** the parsed version contains a hyphen (e.g., `1.0.1-beta`, `2.0.0-rc.1`)
- **THEN** the GitHub Release SHALL be marked as a prerelease

#### Scenario: Stable tag creates stable release

- **WHEN** the parsed version does not contain a hyphen (e.g., `1.0.0`, `1.0.1`)
- **THEN** the GitHub Release SHALL NOT be marked as a prerelease

---

### Requirement: Release workflow runs tests before publishing

The release workflow SHALL execute all test projects before creating the published executable, failing the workflow if any test fails.

#### Scenario: Tests pass before publish

- **WHEN** the release workflow runs
- **THEN** all test projects (Domain.Tests, Infrastructure.Caching.Tests, Infrastructure.Archives.Zip.Tests, IntegrationTests) SHALL be executed in Release configuration
- **AND** if any test fails, the workflow SHALL fail and no GitHub Release SHALL be created

---

### Requirement: Release assets include executable and config

The release workflow SHALL attach the published single-file executable and the appsettings.json configuration file to the GitHub Release.

#### Scenario: Release assets attached

- **WHEN** a GitHub Release is created
- **THEN** it SHALL have `ZipDrive.exe` as an attached asset
- **AND** it SHALL have `appsettings.json` as an attached asset

---

### Requirement: Release workflow uses auto-generated notes

The release workflow SHALL use GitHub's auto-generated release notes to populate the release body.

#### Scenario: Release notes generated

- **WHEN** a GitHub Release is created
- **THEN** the release body SHALL include auto-generated notes from commits since the previous release
