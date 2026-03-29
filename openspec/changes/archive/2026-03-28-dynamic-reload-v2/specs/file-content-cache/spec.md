## ADDED Requirements

### Requirement: RemoveArchive removes all entries for an archive
FileContentCache SHALL expose a `RemoveArchive(string archiveKey)` method that removes all cached file content entries belonging to the specified archive from both memory and disk tiers.

#### Scenario: Remove archive with entries in memory tier
- **WHEN** `RemoveArchive("game.zip")` is called and "game.zip" has 3 cached files in memory tier
- **THEN** it returns 3, all 3 entries are removed, ContainsKey returns false for each

#### Scenario: Remove archive with entries in both tiers
- **WHEN** `RemoveArchive("big.zip")` is called and "big.zip" has 2 memory + 1 disk entries
- **THEN** it returns 3, all entries removed, disk entry queued for async cleanup

#### Scenario: Remove nonexistent archive
- **WHEN** `RemoveArchive("nosuch.zip")` is called
- **THEN** it returns 0 with no side effects

#### Scenario: Remove then re-cache
- **WHEN** `RemoveArchive("game.zip")` is called then ReadAsync triggers a new cache entry
- **THEN** the new entry is cached and registered in the per-archive key index

### Requirement: Per-archive key index using locked HashSet
FileContentCache SHALL maintain a `ConcurrentDictionary<string, HashSet<string>>` with a `Lock` for HashSet mutations. Keys SHALL be registered on every successful BorrowAsync in ReadAsync and WarmAsync.

#### Scenario: Key registered after cache insert
- **WHEN** ReadAsync causes a cache miss and BorrowAsync succeeds
- **THEN** the cache key is registered in the per-archive key index under the archive's key

#### Scenario: Duplicate key registration is idempotent
- **WHEN** the same cache key is registered twice (evict + re-cache cycle)
- **THEN** the HashSet contains the key exactly once (no unbounded growth)

#### Scenario: Remove while entry is borrowed
- **WHEN** `RemoveArchive("game.zip")` is called while an entry is borrowed
- **THEN** the entry is removed from the cache dictionary, the active handle continues working, and storage cleanup is deferred
