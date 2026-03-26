## ADDED Requirements

### Requirement: Reload orchestration
DokanHostedService SHALL orchestrate VFS reload when triggered by directory watcher. The reload sequence SHALL: (1) create a new DI scope, (2) resolve IVirtualFileSystem from the scope, (3) call MountAsync on the new VFS, (4) call SwapAsync on the adapter to atomically replace the VFS, (5) asynchronously dispose the old VfsScope.

#### Scenario: Successful reload
- **WHEN** directory watcher triggers a reload
- **THEN** a new VfsScope is created, mounted, swapped into the adapter, and the old scope is disposed asynchronously

#### Scenario: Reload failure does not disrupt service
- **WHEN** the new VFS fails to mount (e.g., directory inaccessible)
- **THEN** the error SHALL be logged, the new scope SHALL be disposed, and the current VFS SHALL continue serving without interruption

### Requirement: FileSystemWatcher lifecycle
DokanHostedService SHALL create and start a FileSystemWatcher after the initial VFS mount succeeds. The watcher SHALL be disposed in StopAsync.

#### Scenario: Watcher starts after mount
- **WHEN** the initial VFS mount completes successfully
- **THEN** a FileSystemWatcher SHALL be started on the ArchiveDirectory

### Requirement: Old scope disposal
After a successful swap, the old VfsScope SHALL be disposed asynchronously on a background task. Disposal errors SHALL be logged as warnings but SHALL NOT affect the running service.

#### Scenario: Old scope cleaned up in background
- **WHEN** a swap completes
- **THEN** the old VfsScope.DisposeAsync SHALL be called on a background task
- **AND** any disposal error SHALL be logged as a warning
