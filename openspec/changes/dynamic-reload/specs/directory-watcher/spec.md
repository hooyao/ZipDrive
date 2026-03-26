## ADDED Requirements

### Requirement: Watch ArchiveDirectory for ZIP changes
The system SHALL use `FileSystemWatcher` to monitor `Mount:ArchiveDirectory` for `.zip` file additions, deletions, and renames. The watcher SHALL be active for the lifetime of the mounted drive.

#### Scenario: New ZIP file detected
- **WHEN** a `.zip` file is created in `ArchiveDirectory`
- **THEN** a reload SHALL be triggered (after debounce)

#### Scenario: ZIP file deleted
- **WHEN** a `.zip` file is deleted from `ArchiveDirectory`
- **THEN** a reload SHALL be triggered (after debounce)

#### Scenario: ZIP file renamed
- **WHEN** a `.zip` file is renamed in `ArchiveDirectory`
- **THEN** a reload SHALL be triggered (after debounce)

### Requirement: Debounce coalesces rapid changes
The watcher SHALL debounce events with a 2-second quiet period. The debounce timer SHALL reset on each new event. Reload SHALL trigger only after 2 seconds with no new events.

#### Scenario: Batch copy triggers single reload
- **WHEN** 20 ZIP files are copied into `ArchiveDirectory` over 1 second
- **THEN** exactly one reload SHALL occur approximately 2 seconds after the last file event

#### Scenario: Continuous events delay reload
- **WHEN** file events arrive continuously for 10 seconds
- **THEN** reload SHALL not trigger until 2 seconds after the last event

### Requirement: Reload cooldown prevents rapid successive reloads
The system SHALL enforce a 15-second cooldown between reloads. When a debounced reload is ready to fire but the cooldown has not elapsed since the last reload, the reload SHALL be deferred until the cooldown expires. If new file events arrive during the deferred wait, the debounce timer SHALL reset as normal.

#### Scenario: Reload within cooldown is deferred
- **WHEN** a reload completes at T=0s and a debounced reload is ready at T=7s
- **THEN** the reload SHALL be deferred until T=15s

#### Scenario: Events during deferred wait reset debounce
- **WHEN** a reload is deferred waiting for cooldown and new file events arrive
- **THEN** the debounce timer SHALL reset, and the reload SHALL fire at the later of (last event + 2s) or (cooldown expiry)

#### Scenario: No cooldown on first reload
- **WHEN** the first file change is detected after startup
- **THEN** the reload SHALL fire after the 2-second debounce with no cooldown delay

#### Scenario: Rapid successive changes trigger at most one reload per 15 seconds
- **WHEN** file events arrive continuously for 60 seconds
- **THEN** at most 4 reloads SHALL occur (approximately one every 15 seconds after initial debounce)

### Requirement: Watcher cleanup on shutdown
The `FileSystemWatcher` SHALL be disposed when the hosted service stops. No reload SHALL be triggered after shutdown begins.

#### Scenario: Shutdown disposes watcher
- **WHEN** `DokanHostedService.StopAsync` is called
- **THEN** the FileSystemWatcher SHALL be disposed and no further reload attempts SHALL occur
