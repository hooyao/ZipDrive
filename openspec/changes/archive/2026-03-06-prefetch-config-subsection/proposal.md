## Why

Prefetch settings currently live flat in the `Cache` config section with a `Prefetch` prefix on every property name (e.g., `Cache:PrefetchEnabled`, `Cache:PrefetchOnRead`). Since `PrefetchEnabled` is the master switch and `OnRead`/`OnListDirectory` depend on it, a nested subsection makes the hierarchy explicit. Additionally, `CacheOptions` has duplicate prefetch properties that are unused at runtime — only `PrefetchOptions` is consumed. This refactoring also changes `OnRead` to default `true` so users only need to flip `Enabled` to get the recommended configuration.

## What Changes

- **BREAKING** Config keys move from `Cache:PrefetchEnabled` to `Cache:Prefetch:Enabled` (and similarly for all prefetch keys)
- Drop `Prefetch` prefix from `PrefetchOptions` property names (e.g., `PrefetchEnabled` → `Enabled`)
- Change DI binding from `GetSection("Cache")` to `GetSection("Cache:Prefetch")`
- Nest prefetch keys under a `"Prefetch": { ... }` object in `appsettings.jsonc`
- Remove duplicate unused prefetch properties from `CacheOptions`
- Change `OnRead` default from `false` to `true` (active once `Enabled` is flipped on)
- Update all documentation (README, CLAUDE.md) with new config paths

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `prefetch-siblings`: Config key paths change from flat `Cache:Prefetch*` to nested `Cache:Prefetch:*`, property names drop prefix, `OnRead` default changes to `true`

## Impact

- `src/ZipDrive.Domain/Configuration/PrefetchOptions.cs` — rename all properties
- `src/ZipDrive.Infrastructure.Caching/CacheOptions.cs` — remove dead prefetch properties
- `src/ZipDrive.Cli/Program.cs` — update config binding section name
- `src/ZipDrive.Cli/appsettings.jsonc` — restructure to nested object
- `src/ZipDrive.Application/Services/ZipVirtualFileSystem.cs` — update property references
- All test files referencing `PrefetchOptions` properties
- `README.md`, `CLAUDE.md` — update config key documentation
