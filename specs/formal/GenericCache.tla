--------------------------- MODULE GenericCache ---------------------------
(*
 * Formal model of ZipDrive GenericCache concurrency protocol.
 *
 * Models the 5-layer concurrency strategy from CONCURRENCY_STRATEGY.md:
 *   Layer 1: Lock-free ConcurrentDictionary.TryGetValue
 *   Layer 2: Per-key Lazy<Task<T>> materialization (thundering herd prevention)
 *   Layer 3: Global eviction lock (capacity enforcement)
 *   Layer 4: RefCount on CacheEntry (eviction protection)
 *   Layer 5: StorageDisposed flag + FileNotFoundException catch
 *
 * KEY FINDING: The Layer 2 (miss) path after `await lazy.Value` lacks the
 * IsStorageDisposed check and FileNotFoundException catch present in the
 * Layer 1 (hit) path. Between `await lazy.Value` returning and
 * `IncrementRefCount`, the evictor can destroy storage.
 *
 * Set Layer2FixEnabled = FALSE to reproduce the bug.
 * Set Layer2FixEnabled = TRUE  to verify the fix.
 *
 * C# source: src/ZipDrive.Infrastructure.Caching/GenericCache.cs
 *)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    Borrowers,          \* Set of borrower thread IDs (e.g., {b1, b2})
    Keys,               \* Set of cache key IDs (e.g., {k1})
    Layer1FixEnabled,    \* BOOLEAN — toggle Layer 5 guards in the Layer 1 (hit) path
                         \*   FALSE = pre-fix: no IsStorageDisposed check, no FNFE catch
                         \*   TRUE  = current code: both guards present
    Layer2FixEnabled,    \* BOOLEAN — toggle Layer 5 guards in the Layer 2 (miss) path
    MemoryTierSafe       \* BOOLEAN — storage lifecycle model:
                         \*   FALSE = ArrayPool/disk: eviction disposal destroys storage
                         \*   TRUE  = GC byte[]: eviction disposal is a no-op, storage
                         \*           survives as long as any reference exists (Bug #1 fix)

ASSUME Borrowers # {}
ASSUME Keys # {}

VARIABLES
    \* ══════ Per-Key State (models the CacheEntry object) ══════
    inCache,            \* [Keys -> BOOLEAN]  — in ConcurrentDictionary?
    refCount,           \* [Keys -> Int]      — Interlocked ref count
    storageDisposed,    \* [Keys -> BOOLEAN]  — TryMarkStorageDisposed flag
    orphaned,           \* [Keys -> BOOLEAN]  — removed while borrowed?
    storageAlive,       \* [Keys -> BOOLEAN]  — actual byte[]/file valid?
    materializing,      \* [Keys -> BOOLEAN]  — Lazy<Task> in progress?
    cacheSize,          \* Nat               — entries in cache

    \* ══════ Per-Borrower State ══════
    bpc,                \* [Borrowers -> STRING]  — program counter
    bkey,               \* [Borrowers -> Keys]    — target key

    \* ══════ Evictor State ══════
    epc,                \* STRING                 — program counter
    ekey                \* Keys                   — target key

allVars == <<inCache, refCount, storageDisposed, orphaned, storageAlive,
             materializing, cacheSize, bpc, bkey, epc, ekey>>

\* ─── Helpers ────────────────────────────────────────────────────────────

BorrowerStates == { "Start",
                    "L1_TryGet", "L1_IncRef", "L1_CheckDisposed", "L1_Retrieve",
                    "L2_CheckMat", "L2_Wait", "Materialize", "Mat_Release",
                    "L2_IncRef", "L2_CheckDisposed", "L2_Retrieve",
                    "HasHandle", "Return", "Done", "Error_FNFE" }

EvictorStates == { "Idle", "E_CheckRef", "E_TryRemove", "E_PostRemove" }

\* ─── Type Invariant ─────────────────────────────────────────────────────

TypeOK ==
    /\ inCache        \in [Keys -> BOOLEAN]
    /\ refCount       \in [Keys -> Int]
    /\ storageDisposed \in [Keys -> BOOLEAN]
    /\ orphaned       \in [Keys -> BOOLEAN]
    /\ storageAlive   \in [Keys -> BOOLEAN]
    /\ materializing  \in [Keys -> BOOLEAN]
    /\ cacheSize      \in Nat
    /\ bpc            \in [Borrowers -> BorrowerStates]
    /\ bkey           \in [Borrowers -> Keys]
    /\ epc            \in EvictorStates
    /\ ekey           \in Keys

\* ─── Initial State ──────────────────────────────────────────────────────

Init ==
    /\ inCache         = [k \in Keys |-> FALSE]
    /\ refCount        = [k \in Keys |-> 0]
    /\ storageDisposed = [k \in Keys |-> FALSE]
    /\ orphaned        = [k \in Keys |-> FALSE]
    /\ storageAlive    = [k \in Keys |-> FALSE]
    /\ materializing   = [k \in Keys |-> FALSE]
    /\ cacheSize       = 0
    /\ bpc             = [b \in Borrowers |-> "Start"]
    /\ bkey            = [b \in Borrowers |-> CHOOSE k \in Keys : TRUE]
    /\ epc             = "Idle"
    /\ ekey            = CHOOSE k \in Keys : TRUE

\* ═══════════════════════════════════════════════════════════════════════
\*  BORROWER ACTIONS
\*  Models BorrowAsync() in GenericCache.cs
\* ═══════════════════════════════════════════════════════════════════════

\* Choose a key to borrow (non-deterministic)
BStart(b) ==
    /\ bpc[b] = "Start"
    /\ \E k \in Keys :
        /\ bkey' = [bkey EXCEPT ![b] = k]
        /\ bpc'  = [bpc  EXCEPT ![b] = "L1_TryGet"]
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, epc, ekey>>

\* ── Layer 1: ConcurrentDictionary.TryGetValue (lock-free, atomic) ────
\* GenericCache.cs:125
BL1TryGet(b) ==
    /\ bpc[b] = "L1_TryGet"
    /\ bpc' = [bpc EXCEPT ![b] =
        IF inCache[bkey[b]] THEN "L1_IncRef" ELSE "L2_CheckMat"]
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 4: Interlocked.Increment(ref _refCount) ───────────────────
\* GenericCache.cs:128 — CacheEntry.cs:82
\* Pre-fix (Layer1FixEnabled=FALSE): skips CheckDisposed, goes straight to Retrieve.
BL1IncRef(b) ==
    /\ bpc[b] = "L1_IncRef"
    /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ + 1]
    /\ bpc'      = [bpc EXCEPT ![b] =
        IF Layer1FixEnabled THEN "L1_CheckDisposed" ELSE "L1_Retrieve"]
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 5: Volatile.Read(ref _storageDisposed) ────────────────────
\* GenericCache.cs:134 — CacheEntry.cs:111
BL1CheckDisposed(b) ==
    /\ bpc[b] = "L1_CheckDisposed"
    /\ IF storageDisposed[bkey[b]]
       THEN \* Storage gone — decrement and fall through to miss path
            /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ - 1]
            /\ bpc'      = [bpc EXCEPT ![b] = "L2_CheckMat"]
       ELSE \* Proceed to retrieve
            /\ UNCHANGED refCount
            /\ bpc' = [bpc EXCEPT ![b] = "L1_Retrieve"]
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 5: Retrieve storage (may throw FileNotFoundException) ─────
\* GenericCache.cs:153 — catches FNFE at line 156
\* Pre-fix (Layer1FixEnabled=FALSE): no FNFE catch. Reader proceeds to
\* HasHandle with dead storage (Bug #1: data corruption via ArrayPool reuse)
\* or crashes (Bug #2: FileNotFoundException on disk tier).
BL1Retrieve(b) ==
    /\ bpc[b] = "L1_Retrieve"
    /\ IF storageAlive[bkey[b]]
       THEN \* Success — got handle
            /\ bpc' = [bpc EXCEPT ![b] = "HasHandle"]
            /\ UNCHANGED refCount
       ELSE IF Layer1FixEnabled
            THEN \* FIXED: FileNotFoundException caught — fall through to miss
                 /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ - 1]
                 /\ bpc'      = [bpc EXCEPT ![b] = "L2_CheckMat"]
            ELSE \* PRE-FIX BUG: reader accesses destroyed storage
                 \* Bug #1 (ArrayPool): reads corrupted/overwritten data
                 \* Bug #2 (disk): FileNotFoundException propagates
                 /\ bpc' = [bpc EXCEPT ![b] = "Error_FNFE"]
                 /\ UNCHANGED refCount
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 2: Check _materializationTasks (ConcurrentDictionary.GetOrAdd) ─
\* GenericCache.cs:176
\* Models the GetOrAdd + await lazy.Value interaction:
\*   - If entry is in cache and not disposed: another thread already materialized
\*     it — go to L1_IncRef to borrow (models GetOrAdd returning completed Lazy)
\*   - If materializing: join existing Lazy<Task> (wait for factory)
\*   - Otherwise: create new Lazy, invoke factory
\* NOTE: The model uses a single refCount per key. Two concurrent materializations
\* of the same key would corrupt it (they produce separate CacheEntry objects in C#
\* but share one counter here). The inCache guard prevents this by redirecting to
\* the Layer 1 borrow path when the entry already exists.
BL2CheckMat(b) ==
    /\ bpc[b] = "L2_CheckMat"
    /\ IF inCache[bkey[b]] /\ ~storageDisposed[bkey[b]]
       THEN \* Entry materialized by another thread — borrow via Layer 1
            /\ bpc' = [bpc EXCEPT ![b] = "L1_IncRef"]
            /\ UNCHANGED materializing
       ELSE IF materializing[bkey[b]]
            THEN \* Another thread is materializing — join existing Lazy<Task>
                 /\ bpc' = [bpc EXCEPT ![b] = "L2_Wait"]
                 /\ UNCHANGED materializing
            ELSE \* First thread — start new Lazy<Task> + invoke factory
                 /\ materializing' = [materializing EXCEPT ![bkey[b]] = TRUE]
                 /\ bpc' = [bpc EXCEPT ![b] = "Materialize"]
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, cacheSize, bkey, epc, ekey>>

\* ── Layer 2: await lazy.Value (blocks until Task completes) ──────────
\* GenericCache.cs:183 — continuation scheduled when Task completes
BL2Wait(b) ==
    /\ bpc[b] = "L2_Wait"
    /\ ~materializing[bkey[b]]  \* Task completed (factory returned)
    /\ bpc' = [bpc EXCEPT ![b] = "L2_IncRef"]
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, bkey, epc, ekey>>

\* ── Materialize: factory + store in cache with temp RefCount hold ────
\* GenericCache.cs:250-270  (MaterializeAndCacheAsync)
\* Steps combined: new CacheEntry → IncrementRefCount → _cache[key] = entry → Add size
\* Atomic because no other process can see the entry until it's in the dict.
BMaterialize(b) ==
    /\ bpc[b] = "Materialize"
    /\ inCache'         = [inCache EXCEPT ![bkey[b]] = TRUE]
    /\ refCount'        = [refCount EXCEPT ![bkey[b]] = 1]  \* temp hold
    /\ storageDisposed' = [storageDisposed EXCEPT ![bkey[b]] = FALSE]
    /\ orphaned'        = [orphaned EXCEPT ![bkey[b]] = FALSE]
    /\ storageAlive'    = [storageAlive EXCEPT ![bkey[b]] = TRUE]
    /\ cacheSize'       = cacheSize + 1
    /\ bpc'             = [bpc EXCEPT ![b] = "Mat_Release"]
    /\ UNCHANGED <<materializing, bkey, epc, ekey>>

\* ── Release temp RefCount + signal Task completion ───────────────────
\* GenericCache.cs:298 (DecrementRefCount) + return entry (Task completes)
\* materializing := FALSE models the Task completion that unblocks waiters.
BMatRelease(b) ==
    /\ bpc[b] = "Mat_Release"
    /\ refCount'     = [refCount EXCEPT ![bkey[b]] = @ - 1]
    /\ materializing' = [materializing EXCEPT ![bkey[b]] = FALSE]
    /\ bpc'          = [bpc EXCEPT ![b] = "L2_IncRef"]
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   cacheSize, bkey, epc, ekey>>

\* ── Layer 2: IncrementRefCount for handle (after await lazy.Value) ───
\* GenericCache.cs:186
\* BUG: Code goes directly to Retrieve without checking storageDisposed.
\* FIX: When Layer2FixEnabled, insert a storageDisposed check first.
BL2IncRef(b) ==
    /\ bpc[b] = "L2_IncRef"
    /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ + 1]
    /\ bpc'      = [bpc EXCEPT ![b] =
        IF Layer2FixEnabled THEN "L2_CheckDisposed" ELSE "L2_Retrieve"]
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 2 FIX: Check storageDisposed (mirrors L1_CheckDisposed) ───
\* This action only exists when Layer2FixEnabled = TRUE.
BL2CheckDisposed(b) ==
    /\ bpc[b] = "L2_CheckDisposed"
    /\ Layer2FixEnabled
    /\ IF storageDisposed[bkey[b]]
       THEN /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ - 1]
            /\ bpc'      = [bpc EXCEPT ![b] = "L2_CheckMat"]  \* retry
       ELSE /\ UNCHANGED refCount
            /\ bpc' = [bpc EXCEPT ![b] = "L2_Retrieve"]
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── Layer 2: Retrieve storage ────────────────────────────────────────
\* GenericCache.cs:188  _storageStrategy.Retrieve(entry.Stored)
\* BUG:  No try/catch for FileNotFoundException in Layer 2 path!
\* FIX:  When Layer2FixEnabled, catch FNFE and retry like Layer 1.
BL2Retrieve(b) ==
    /\ bpc[b] = "L2_Retrieve"
    /\ IF storageAlive[bkey[b]]
       THEN \* Success — got handle
            /\ bpc' = [bpc EXCEPT ![b] = "HasHandle"]
            /\ UNCHANGED refCount
       ELSE IF Layer2FixEnabled
            THEN \* FIX: catch FNFE, decrement, retry
                 /\ refCount' = [refCount EXCEPT ![bkey[b]] = @ - 1]
                 /\ bpc'      = [bpc EXCEPT ![b] = "L2_CheckMat"]
            ELSE \* BUG: unhandled FileNotFoundException!
                 /\ bpc' = [bpc EXCEPT ![b] = "Error_FNFE"]
                 /\ UNCHANGED refCount
    /\ UNCHANGED <<inCache, storageDisposed, orphaned, storageAlive,
                   materializing, cacheSize, bkey, epc, ekey>>

\* ── HasHandle: thread is actively using data ─────────────────────────
\* The safety property NoUseAfterDispose asserts storageAlive here.
BHasHandle(b) ==
    /\ bpc[b] = "HasHandle"
    /\ bpc' = [bpc EXCEPT ![b] = "Return"]
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, bkey, epc, ekey>>

\* ── Return: handle disposal (CacheHandle.Dispose → Return callback) ──
\* GenericCache.cs:203-223
\* Interlocked.Decrement + orphan cleanup (TryMarkStorageDisposed + dispose)
BReturn(b) ==
    /\ bpc[b] = "Return"
    /\ LET k        == bkey[b]
           newRef   == refCount[k] - 1
           cleanup  == newRef = 0 /\ orphaned[k] /\ ~storageDisposed[k]
       IN
       /\ refCount'        = [refCount EXCEPT ![k] = newRef]
       /\ storageDisposed' = [storageDisposed EXCEPT ![k] =
            IF cleanup THEN TRUE ELSE @]
       /\ storageAlive'    = [storageAlive EXCEPT ![k] =
            IF cleanup /\ ~MemoryTierSafe THEN FALSE ELSE @]
    /\ bpc' = [bpc EXCEPT ![b] = "Done"]
    /\ UNCHANGED <<inCache, orphaned, materializing, cacheSize, bkey, epc, ekey>>

\* ═══════════════════════════════════════════════════════════════════════
\*  EVICTOR ACTIONS
\*  Models TryEvictEntry() in GenericCache.cs:430-481
\*  Non-deterministic: can attempt eviction of any entry with inCache=TRUE.
\*  Over-approximation (more behaviors than real system) — safe for safety.
\* ═══════════════════════════════════════════════════════════════════════

\* Choose a target key to attempt eviction
EStart ==
    /\ epc = "Idle"
    /\ \E k \in Keys :
        /\ inCache[k]
        /\ ekey' = k
        /\ epc'  = "E_CheckRef"
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, bpc, bkey>>

\* Check RefCount == 0 (Volatile.Read)
\* GenericCache.cs:440
ECheckRef ==
    /\ epc = "E_CheckRef"
    /\ epc' = IF refCount[ekey] = 0 THEN "E_TryRemove" ELSE "Idle"
    /\ UNCHANGED <<inCache, refCount, storageDisposed, orphaned,
                   storageAlive, materializing, cacheSize, bpc, bkey, ekey>>

\* ConcurrentDictionary.TryRemove (atomic)
\* GenericCache.cs:446
ETryRemove ==
    /\ epc = "E_TryRemove"
    /\ IF inCache[ekey]
       THEN /\ inCache'   = [inCache EXCEPT ![ekey] = FALSE]
            /\ cacheSize'  = cacheSize - 1
            /\ epc'        = "E_PostRemove"
       ELSE /\ epc' = "Idle"
            /\ UNCHANGED <<inCache, cacheSize>>
    /\ UNCHANGED <<refCount, storageDisposed, orphaned, storageAlive,
                   materializing, bpc, bkey, ekey>>

\* Post-remove decision: orphan or dispose
\* GenericCache.cs:458-471
\* Between ECheckRef (refCount=0) and here, a borrower may have incremented.
\*
\* MemoryTierSafe models the Bug #1 fix (GC byte[] vs ArrayPool):
\*   FALSE (ArrayPool/disk): disposal destroys storage (storageAlive=FALSE)
\*   TRUE  (GC byte[]): disposal is a no-op for the underlying data — the byte[]
\*         survives as long as any MemoryStream reference exists (GC guarantee)
EPostRemove ==
    /\ epc = "E_PostRemove"
    /\ IF refCount[ekey] > 0
       THEN \* RACE: borrower got in — mark orphaned, defer cleanup
            /\ orphaned' = [orphaned EXCEPT ![ekey] = TRUE]
            /\ UNCHANGED <<storageDisposed, storageAlive>>
       ELSE \* No borrowers — TryMarkStorageDisposed + dispose
            /\ storageDisposed' = [storageDisposed EXCEPT ![ekey] = TRUE]
            /\ storageAlive'    = [storageAlive EXCEPT ![ekey] =
                IF MemoryTierSafe THEN @ ELSE FALSE]
            /\ UNCHANGED orphaned
    /\ epc' = "Idle"
    /\ UNCHANGED <<inCache, refCount, materializing, cacheSize, bpc, bkey, ekey>>

\* ═══════════════════════════════════════════════════════════════════════
\*  SPECIFICATION
\* ═══════════════════════════════════════════════════════════════════════

BorrowerAction(b) ==
    \/ BStart(b)
    \/ BL1TryGet(b)
    \/ BL1IncRef(b)
    \/ BL1CheckDisposed(b)
    \/ BL1Retrieve(b)
    \/ BL2CheckMat(b)
    \/ BL2Wait(b)
    \/ BMaterialize(b)
    \/ BMatRelease(b)
    \/ BL2IncRef(b)
    \/ BL2CheckDisposed(b)
    \/ BL2Retrieve(b)
    \/ BHasHandle(b)
    \/ BReturn(b)

EvictorAction ==
    \/ EStart
    \/ ECheckRef
    \/ ETryRemove
    \/ EPostRemove

\* System quiescence: all borrowers done/errored, evictor idle → stutter.
\* Prevents TLC from reporting a spurious "deadlock" at terminal states.
Terminated ==
    /\ \A b \in Borrowers : bpc[b] \in {"Done", "Error_FNFE"}
    /\ epc = "Idle"
    /\ UNCHANGED allVars

Next ==
    \/ \E b \in Borrowers : BorrowerAction(b)
    \/ EvictorAction
    \/ Terminated

Spec     == Init /\ [][Next]_allVars
FairSpec == Spec /\ WF_allVars(Next)

\* ═══════════════════════════════════════════════════════════════════════
\*  SAFETY PROPERTIES (checked as invariants)
\* ═══════════════════════════════════════════════════════════════════════

\* CRITICAL: No thread holds a handle to destroyed storage.
\* If a borrower is in HasHandle, the underlying storage must be alive.
NoUseAfterDispose ==
    \A b \in Borrowers :
        bpc[b] = "HasHandle" => storageAlive[bkey[b]]

\* BUG DETECTOR: Layer 2 path must never reach the unhandled FNFE state.
\* With Layer2FixEnabled = FALSE, TLC finds a counterexample.
NoUnhandledException ==
    \A b \in Borrowers :
        bpc[b] # "Error_FNFE"

\* RefCount is non-negative for entries that haven't been re-materialized.
RefCountNonNeg ==
    \A k \in Keys : refCount[k] >= 0

\* Symmetry set for model checking optimization
BorrowerSymmetry == Permutations(Borrowers)

\* ═══════════════════════════════════════════════════════════════════════
\*  LIVENESS PROPERTIES (checked as temporal properties with FairSpec)
\* ═══════════════════════════════════════════════════════════════════════

\* Every borrower eventually terminates (reaches Done or Error_FNFE).
AllBorrowersTerminate ==
    \A b \in Borrowers : <>(bpc[b] \in {"Done", "Error_FNFE"})

===========================================================================
