## MODIFIED Requirements

### Requirement: Dual-tier cache DI registration
The CLI SHALL register cache and VFS services with appropriate DI lifetimes. Shared infrastructure (TimeProvider, IZipReaderFactory, IEvictionPolicy, IFilenameEncodingDetector, configuration options) SHALL be registered as Singleton. VFS-instance services (IArchiveTrie, IPathResolver, IArchiveDiscovery, IArchiveStructureStore, IArchiveStructureCache, IFileContentCache, IVirtualFileSystem) SHALL be registered as Scoped to enable per-reload instance creation.

#### Scenario: DI container wires scoped VFS services
- **WHEN** a new DI scope is created and IVirtualFileSystem is resolved
- **THEN** a fresh instance graph (ArchiveTrie, StructureCache, FileContentCache, VFS) SHALL be created within that scope

#### Scenario: Singleton services shared across scopes
- **WHEN** multiple DI scopes resolve singleton services
- **THEN** all scopes SHALL share the same TimeProvider, IZipReaderFactory, IEvictionPolicy, and IFilenameEncodingDetector instances

## REMOVED Requirements

### Requirement: Standard .NET Generic Host
**Reason**: Replaced by modified requirement below. The host setup remains but CacheMaintenanceService is replaced by per-scope maintenance timers.
**Migration**: CacheMaintenanceService HostedService registration removed from Program.cs. Cache maintenance is now handled by VfsScope internal PeriodicTimer.
