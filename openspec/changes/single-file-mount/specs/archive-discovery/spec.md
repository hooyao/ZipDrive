## ADDED Requirements

### Requirement: Empty directory detection
When `DiscoverAsync` returns zero archives for a given directory, callers SHALL be able to detect this condition and display an appropriate warning.

#### Scenario: Directory with no archives
- **WHEN** `DiscoverAsync` is called on a directory containing no supported archive files
- **THEN** an empty list SHALL be returned (existing behavior)
- **AND** the caller (`DokanHostedService`) SHALL display a WARNING notice listing supported formats and noting dynamic reload availability
