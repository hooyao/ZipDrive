## Context

Prefetch config is currently flat in the `Cache` section with `Prefetch`-prefixed property names. The `PrefetchOptions` class binds from `GetSection("Cache")` and relies on the prefix to match properties. `CacheOptions` has duplicate prefetch properties that are never read at runtime. The `OnRead` default was recently changed to `false` but should be `true` so enabling prefetch requires only one toggle.

## Goals / Non-Goals

**Goals:**
- Nest prefetch config under `Cache:Prefetch` subsection in JSON
- Drop `Prefetch` prefix from `PrefetchOptions` property names
- Remove dead prefetch properties from `CacheOptions`
- Set `OnRead` default to `true`
- Update all references, tests, and documentation

**Non-Goals:**
- Changing prefetch behavior or algorithms
- Adding new config options
- Backwards compatibility with old flat keys (breaking change accepted)

## Decisions

### 1. Bind `PrefetchOptions` from `Cache:Prefetch` subsection
**Rationale**: .NET config binding naturally supports nested sections via `GetSection("Cache:Prefetch")`. Property names match JSON keys without prefix.

### 2. Drop `Prefetch` prefix from all property names
**Rationale**: With the class already named `PrefetchOptions` and bound from a `Prefetch` section, the prefix is redundant. `Enabled` is clearer than `PrefetchEnabled` in context.

| Old Property | New Property |
|---|---|
| `PrefetchEnabled` | `Enabled` |
| `PrefetchOnRead` | `OnRead` |
| `PrefetchOnListDirectory` | `OnListDirectory` |
| `PrefetchFileSizeThresholdMb` | `FileSizeThresholdMb` |
| `PrefetchMaxFiles` | `MaxFiles` |
| `PrefetchMaxDirectoryFiles` | `MaxDirectoryFiles` |
| `PrefetchFillRatioThreshold` | `FillRatioThreshold` |
| `PrefetchFileSizeThresholdBytes` | `FileSizeThresholdBytes` |

### 3. `OnRead` defaults to `true`
**Rationale**: The recommended posture is `Enabled=false, OnRead=true, OnListDirectory=false`. When a user flips `Enabled` to `true`, read-triggered prefetch activates immediately without a second toggle. `OnListDirectory` stays `false` because image viewers (FastStone) and file managers enumerate sibling directories, causing runaway prefetch.

### 4. Remove dead `CacheOptions` prefetch properties
**Rationale**: `CacheOptions` lines 142-185 duplicate prefetch properties but are never read at runtime. Only `PrefetchOptions` is injected into `ZipVirtualFileSystem`. Removing dead code eliminates confusion.

## Risks / Trade-offs

- **[Breaking config keys]** → Users with existing `appsettings.jsonc` overrides using `Cache:PrefetchEnabled` must update to `Cache:Prefetch:Enabled`. Acceptable since this is a pre-1.0 project with no external users relying on config stability.
- **[CLI override length]** → `--Cache:Prefetch:Enabled=true` is slightly longer than `--Cache:PrefetchEnabled=true`. Minimal impact.
