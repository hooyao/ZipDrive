## ADDED Requirements

### Requirement: Reference counting on adapter callbacks
DokanFileSystemAdapter SHALL maintain an atomic active-operation count. Each Dokan callback SHALL increment the count on entry and decrement it on exit (in a finally block).

#### Scenario: Count tracks in-flight operations
- **WHEN** a Dokan callback (e.g., ReadFile, FindFiles) begins executing
- **THEN** the active count SHALL be incremented before any VFS call
- **AND** the active count SHALL be decremented after the VFS call completes (including on exception)

### Requirement: Drain mode rejects new requests
When drain mode is active, new Dokan callbacks SHALL return `NtStatus.DeviceBusy` without incrementing the active count. A double-check after increment SHALL handle the race between drain flag set and increment.

#### Scenario: New request during drain
- **WHEN** drain mode is active and a new Dokan callback arrives
- **THEN** it SHALL return `NtStatus.DeviceBusy` without accessing the VFS

#### Scenario: Race between drain and increment
- **WHEN** a callback passes the initial drain check but drain activates before increment
- **THEN** the callback SHALL detect drain after incrementing, decrement, and return `NtStatus.DeviceBusy`

### Requirement: SwapAsync performs atomic VFS replacement
DokanFileSystemAdapter SHALL provide a `SwapAsync(IVirtualFileSystem newVfs, TimeSpan timeout)` method that: (1) activates drain mode, (2) waits for active count to reach zero or timeout, (3) replaces the VFS reference atomically, (4) deactivates drain mode, (5) returns the old VFS reference.

#### Scenario: Successful drain and swap
- **WHEN** `SwapAsync` is called with a new VFS and all in-flight operations complete within timeout
- **THEN** the old VFS is returned, the new VFS is installed, and drain mode is deactivated

#### Scenario: Drain timeout forces swap
- **WHEN** `SwapAsync` is called and active count does not reach zero within the timeout period
- **THEN** the swap SHALL proceed anyway, a warning SHALL be logged with the remaining active count, and the old VFS SHALL be returned

### Requirement: VFS reference is volatile
The `_vfs` field in DokanFileSystemAdapter SHALL be declared volatile. Each Dokan callback SHALL capture a local snapshot (`var vfs = _vfs`) and use that snapshot for the entire callback duration.

#### Scenario: Callback uses consistent VFS reference
- **WHEN** a Dokan callback captures the VFS reference and a swap occurs mid-execution
- **THEN** the callback SHALL continue using its captured snapshot without disruption
