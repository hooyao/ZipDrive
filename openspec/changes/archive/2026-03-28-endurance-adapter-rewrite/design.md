## Context

The full endurance test design is in `src/Docs/DYNAMIC_RELOAD_DESIGN.md` Section 14.10 (v2.0, reviewed by 3 specialized agents). This artifact summarizes the key decisions for the adapter rewrite.

## Goals / Non-Goals

**Goals:**
- All endurance test I/O goes through `DokanFileSystemAdapter.Guarded*Async` methods
- Adapter constructed without Dokany runtime (lightweight, test-friendly)
- DynamicReloadSuite exercises 10 categories of logical user scenarios
- Post-run assertions detect leaks, stale state, and degradation

**Non-Goals:**
- Testing Dokan native memory types (ReadOnlyNativeMemory) — those require Dokany kernel driver
- Changing adapter production behavior — Guarded methods are purely additive

## Decisions

### D1: Guarded methods delegate directly to VFS (no adapter-level guard)

The old `huyao/feat/dynamic-reload` branch had an adapter-level drain guard (EnterGuard/ExitGuard). Our architecture moved drain logic into `ArchiveNode` in the VFS layer. The adapter has a stable VFS reference. So Guarded methods are simple pass-through:

```csharp
public Task<int> GuardedReadFileAsync(string path, byte[] buf, long offset, CancellationToken ct)
    => _vfs.ReadFileAsync(path, buf, offset, ct);
```

This is intentional — the adapter is a thin translation layer, and the drain/guard logic lives where it belongs (per-archive in the VFS).

### D2: EnduranceSuiteBase takes `DokanFileSystemAdapter` (not interface)

Using the concrete type (not an interface) because:
- Tests need the specific `Guarded*Async` methods which are on the concrete class
- No need for mockability at this level — endurance tests use real instances
- Matches the old branch's pattern exactly

### D3: DynamicReloadSuite uses both adapter (reads) and IArchiveManager (lifecycle)

File reads go through `Adapter.GuardedReadFileAsync`. Archive add/remove goes through `IArchiveManager.AddArchiveAsync/RemoveArchiveAsync`. This reflects how the real system works: Dokan callbacks → adapter → VFS for reads; FileSystemWatcher → hosted service → IArchiveManager for lifecycle.

## Risks / Trade-offs

- **[Test coupling to concrete type]** → Acceptable for endurance tests which test the real stack
- **[Guarded methods are trivial pass-through]** → True, but they establish the pattern for future adapter-level logic and match the user's explicit requirement
