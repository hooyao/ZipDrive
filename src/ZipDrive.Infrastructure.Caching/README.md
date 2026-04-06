# ZipDrive V3 - Caching Layer

**Status:** ✅ Design Complete, Ready for Implementation

## Quick Overview

The caching layer is **THE CORE** of ZipDrive. It solves the fundamental mismatch between:
- **ZIP archives**: Sequential access only (compressed streams)
- **Windows file system**: Random access required (read at any offset)

## The Solution

**Dual-tier caching** that materializes (fully decompresses) ZIP entries into random-access storage:

```
Small Files (< 50MB)     →  Memory Tier (ConcurrentDictionary, byte[] in RAM)
Large Files (≥ 50MB)     →  Disk Tier (MemoryMappedFile on temp disk)
Both Tiers               →  Same IEvictionPolicy interface (unified architecture)
```

## Key Features

✅ **Materialization**: Sequential → Random access conversion
✅ **Dual-Tier**: Memory for small, disk for large files
✅ **TTL**: Automatic expiration (default: 30 minutes)
✅ **Capacity Limits**: Enforced via eviction (2GB RAM + 10GB disk)
✅ **Pluggable Eviction**: Strategy pattern (LRU default, LFU/Size-First future)
✅ **Async Cleanup**: < 1ms eviction latency (mark + async cleanup)
✅ **Configurable**: All limits tuneable via appsettings.json

## Architecture

```
┌─────────────────────────────────────────────┐
│           IFileCache (Interface)             │
│   GetOrAddAsync(key, size, ttl, factory)    │
└─────────────────────────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│        DualTierFileCache (Coordinator)       │
│  Routes to tier based on file size           │
└─────────────────────────────────────────────┘
        ↓                          ↓
┌──────────────────┐    ┌──────────────────────┐
│  MemoryTierCache │    │   DiskTierCache      │
│  (< 50MB)        │    │   (≥ 50MB)           │
│                  │    │                      │
│  ConcurrentDict  │    │  MemoryMappedFile    │
│  byte[] storage  │    │  Temp file storage   │
│  IEvictionPolicy │    │  IEvictionPolicy     │
│  (unified!)      │    │  Async cleanup       │
└──────────────────┘    └──────────────────────┘
          ↓                        ↓
┌─────────────────────────────────────────────┐
│           IEvictionPolicy (Pluggable)        │
│  - LRU (default)                             │
│  - LFU (future)                              │
│  - Size-First (future)                       │
└─────────────────────────────────────────────┘
```

**Why NOT built-in MemoryCache?**
- ❌ No pluggable eviction policy
- ❌ Unpredictable compaction when full
- ❌ Can't control which entries get evicted
- ❌ Priority-based eviction, not LRU

**Our Solution:** Simple custom cache (~60 lines) with `ConcurrentDictionary` and unified `IEvictionPolicy` for both tiers.

## Performance Without vs With Cache

| Scenario | Without Cache | With Cache | Improvement |
|----------|---------------|------------|-------------|
| **Video player** (1000 random reads) | 1000 decompressions | 1 decompression | **1000x faster** |
| **Open file at end** (100MB) | Decompress 0→100MB | Seek to end (instant) | **100MB work → 0** |
| **Text editor** | Minutes to open | Instant | **Usable vs unusable** |

## Configuration Example

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,       // 2GB RAM
    "DiskCacheSizeMb": 10240,        // 10GB disk
    "SmallFileCutoffMb": 50,         // Tier routing cutoff
    "TempDirectory": null,           // System temp
    "DefaultTtlMinutes": 30          // 30 min expiration
  }
}
```

## Documentation

📖 **Full Design Doc**: See [CACHING_DESIGN.md](../Docs/CACHING_DESIGN.md) (2500+ lines)

📖 **Memory-Tier Evolution Analysis**: See [MEMORY_CACHE_ARCHITECTURE_DESIGN.md](../Docs/MEMORY_CACHE_ARCHITECTURE_DESIGN.md) (500+ lines)

Covers:
- Problem statement and requirements
- Architecture and data flow
- Component design with code examples
- Testing strategy
- All design decisions (resolved)

## Implementation Status

- [x] Design complete
- [x] All questions resolved
- [x] Architecture approved
- [ ] IFileCache interface (Phase 1)
- [ ] MemoryTierCache (Phase 1)
- [ ] DiskTierCache (Phase 1)
- [ ] DualTierFileCache (Phase 1)
- [ ] IEvictionPolicy interface (Phase 2)
- [ ] Async cleanup (Phase 2)
- [ ] Metrics integration (Phase 3)

## Why This Matters

**Without this cache, ZipDrive is completely unusable.**

Every read operation would require:
1. Open ZIP
2. Decompress from beginning to offset
3. Read requested bytes
4. Discard everything

With cache:
1. First access: Decompress once, store
2. All future accesses: Seek + read (instant)

**Result:** Comparable to native file system performance.
