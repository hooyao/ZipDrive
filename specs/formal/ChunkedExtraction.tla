------------------------- MODULE ChunkedExtraction -------------------------
(*
 * Formal model of ZipDrive ChunkedFileEntry / ChunkedStream synchronization.
 *
 * Models the writer (background ExtractAsync task) that decompresses data
 * in sequential chunks to an NTFS sparse file, and multiple concurrent
 * readers (ChunkedStream instances) that read from the same file.
 *
 * Synchronization protocol:
 *   Writer:  write data → flush → Volatile.Write(chunkReady=1) → TCS.TrySetResult
 *   Reader:  Volatile.Read(chunkReady) → [await TCS if not ready] → open file → read
 *
 * The Volatile.Write provides a store-release barrier ensuring file writes
 * are visible before the ready flag. The reader's Volatile.Read provides a
 * load-acquire barrier ensuring the flag check precedes the file read.
 * ChunkedStream uses bufferSize=1 to prevent the FileStream internal buffer
 * from caching zeros from unwritten sparse regions.
 *
 * C# sources:
 *   src/ZipDrive.Infrastructure.Caching/ChunkedFileEntry.cs
 *   src/ZipDrive.Infrastructure.Caching/ChunkedStream.cs
 *)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    NumChunks,      \* Number of chunks (e.g., 3)
    Readers         \* Set of reader thread IDs (e.g., {r1, r2})

ASSUME NumChunks > 0
ASSUME Readers # {}

ChunkIndices == 0 .. (NumChunks - 1)

VARIABLES
    \* ══════ Per-Chunk State ══════
    chunkState,     \* [ChunkIndices -> {"empty", "flushed", "ready"}]
    tcsSignaled,    \* [ChunkIndices -> BOOLEAN]

    \* ══════ Writer State ══════
    wpc,            \* STRING — writer program counter
    wIdx,           \* 0..NumChunks — current chunk index (NumChunks = done)

    \* ══════ Per-Reader State ══════
    rpc,            \* [Readers -> STRING]
    rTarget,        \* [Readers -> ChunkIndices] — chunk being read
    rDone           \* [Readers -> BOOLEAN] — reader completed successfully

allVars == <<chunkState, tcsSignaled, wpc, wIdx, rpc, rTarget, rDone>>

\* ─── Type Invariant ─────────────────────────────────────────────────────

WriterStates == {"Idle", "Writing", "Flushing", "MarkReady", "Advance", "Done", "Failed"}
ReaderStates == {"Start", "CheckReady", "Waiting", "Reading", "Done"}

TypeOK ==
    /\ chunkState  \in [ChunkIndices -> {"empty", "flushed", "ready"}]
    /\ tcsSignaled \in [ChunkIndices -> BOOLEAN]
    /\ wpc         \in WriterStates
    /\ wIdx        \in 0..NumChunks
    /\ rpc         \in [Readers -> ReaderStates]
    /\ rTarget     \in [Readers -> ChunkIndices]
    /\ rDone       \in [Readers -> BOOLEAN]

\* ─── Initial State ──────────────────────────────────────────────────────

Init ==
    /\ chunkState  = [i \in ChunkIndices |-> "empty"]
    /\ tcsSignaled = [i \in ChunkIndices |-> FALSE]
    /\ wpc         = "Idle"
    /\ wIdx        = 0
    /\ rpc         = [r \in Readers |-> "Start"]
    /\ rTarget     = [r \in Readers |-> 0]  \* will be set in Start
    /\ rDone       = [r \in Readers |-> FALSE]

\* ═══════════════════════════════════════════════════════════════════════
\*  WRITER ACTIONS (background ExtractAsync task)
\*  ChunkedFileEntry.cs:139-227
\* ═══════════════════════════════════════════════════════════════════════

\* Start extraction
WStart ==
    /\ wpc = "Idle"
    /\ wIdx < NumChunks
    /\ wpc' = "Writing"
    /\ UNCHANGED <<chunkState, tcsSignaled, wIdx, rpc, rTarget, rDone>>

\* Write chunk data to sparse backing file
\* ChunkedFileEntry.cs:165-175 (decompressedStream.ReadAsync + fs.WriteAsync)
WWrite ==
    /\ wpc = "Writing"
    /\ wIdx < NumChunks
    /\ chunkState' = [chunkState EXCEPT ![wIdx] = "flushed"]
    /\ wpc' = "Flushing"
    /\ UNCHANGED <<tcsSignaled, wIdx, rpc, rTarget, rDone>>

\* Flush data to disk
\* ChunkedFileEntry.cs:189 (fs.FlushAsync)
WFlush ==
    /\ wpc = "Flushing"
    /\ wpc' = "MarkReady"
    /\ UNCHANGED <<chunkState, tcsSignaled, wIdx, rpc, rTarget, rDone>>

\* Mark chunk ready: Volatile.Write + TCS signal
\* ChunkedFileEntry.cs:104-109 (MarkChunkReady)
\* Order: Volatile.Write(chunkReady=1) → Interlocked.Add → TCS.TrySetResult
\* The Volatile.Write provides a store-release barrier, ensuring all prior
\* writes (file data + flush) are ordered before the ready flag.
WMarkReady ==
    /\ wpc = "MarkReady"
    /\ wIdx < NumChunks
    /\ chunkState' = [chunkState EXCEPT ![wIdx] = "ready"]
    /\ tcsSignaled' = [tcsSignaled EXCEPT ![wIdx] = TRUE]
    /\ wpc' = "Advance"
    /\ UNCHANGED <<wIdx, rpc, rTarget, rDone>>

\* Advance to next chunk
WAdvance ==
    /\ wpc = "Advance"
    /\ wIdx' = wIdx + 1
    /\ wpc'  = IF wIdx + 1 < NumChunks THEN "Writing" ELSE "Done"
    /\ UNCHANGED <<chunkState, tcsSignaled, rpc, rTarget, rDone>>

\* Writer failure (e.g., corrupt archive, cancellation)
\* ChunkedFileEntry.cs:201-213 (CancelPendingChunks / FailPendingChunks)
WFail ==
    /\ wpc \in {"Idle", "Writing", "Flushing"}
    /\ wpc' = "Failed"
    \* Signal all unfinished chunks as failed (TCS.TrySetCanceled)
    /\ tcsSignaled' = [i \in ChunkIndices |->
        IF chunkState[i] # "ready" THEN TRUE ELSE tcsSignaled[i]]
    /\ UNCHANGED <<chunkState, wIdx, rpc, rTarget, rDone>>

\* ═══════════════════════════════════════════════════════════════════════
\*  READER ACTIONS (ChunkedStream.Read / EnsureChunkReadyAsync)
\*  ChunkedStream.cs
\* ═══════════════════════════════════════════════════════════════════════

\* Reader chooses a target chunk (non-deterministic)
RStart(r) ==
    /\ rpc[r] = "Start"
    /\ \E c \in ChunkIndices :
        /\ rTarget' = [rTarget EXCEPT ![r] = c]
        /\ rpc'     = [rpc EXCEPT ![r] = "CheckReady"]
    /\ UNCHANGED <<chunkState, tcsSignaled, wpc, wIdx, rDone>>

\* Check if chunk is ready via Volatile.Read
\* ChunkedFileEntry.cs:82-83 (IsChunkReady)
RCheckReady(r) ==
    /\ rpc[r] = "CheckReady"
    /\ IF chunkState[rTarget[r]] = "ready"
       THEN rpc' = [rpc EXCEPT ![r] = "Reading"]
       ELSE rpc' = [rpc EXCEPT ![r] = "Waiting"]
    /\ UNCHANGED <<chunkState, tcsSignaled, wpc, wIdx, rTarget, rDone>>

\* Wait for TCS signal (WaitForChunkAsync)
\* ChunkedFileEntry.cs:89-98
\* In C#, TCS.TrySetCanceled/TrySetException causes await to throw.
\* Only TCS.TrySetResult (from MarkChunkReady) allows the reader to proceed.
\* After waking, re-check chunk state: if not "ready", writer failed.
RWait(r) ==
    /\ rpc[r] = "Waiting"
    /\ tcsSignaled[rTarget[r]]  \* blocks until TCS signal (result, cancel, or error)
    /\ IF chunkState[rTarget[r]] = "ready"
       THEN rpc' = [rpc EXCEPT ![r] = "Reading"]   \* Normal: proceed to read
       ELSE rpc' = [rpc EXCEPT ![r] = "Done"]       \* Writer failed: throw in C#
    /\ UNCHANGED <<chunkState, tcsSignaled, wpc, wIdx, rTarget, rDone>>

\* Read data from backing file
\* At this point, the chunk is guaranteed ready (data flushed + flag set).
\* ChunkedStream uses unbuffered FileStream (bufferSize=1) to prevent
\* stale reads from sparse file regions.
RRead(r) ==
    /\ rpc[r] = "Reading"
    /\ rDone' = [rDone EXCEPT ![r] = TRUE]
    /\ rpc'   = [rpc EXCEPT ![r] = "Done"]
    /\ UNCHANGED <<chunkState, tcsSignaled, wpc, wIdx, rTarget>>

\* ═══════════════════════════════════════════════════════════════════════
\*  SPECIFICATION
\* ═══════════════════════════════════════════════════════════════════════

WriterAction ==
    \/ WStart
    \/ WWrite
    \/ WFlush
    \/ WMarkReady
    \/ WAdvance
    \/ WFail

ReaderAction(r) ==
    \/ RStart(r)
    \/ RCheckReady(r)
    \/ RWait(r)
    \/ RRead(r)

Terminated ==
    /\ wpc \in {"Done", "Failed"}
    /\ \A r \in Readers : rpc[r] = "Done"
    /\ UNCHANGED allVars

Next ==
    \/ WriterAction
    \/ \E r \in Readers : ReaderAction(r)
    \/ Terminated

Spec     == Init /\ [][Next]_allVars
FairSpec == Spec /\ WF_allVars(Next)

\* ═══════════════════════════════════════════════════════════════════════
\*  SAFETY PROPERTIES
\* ═══════════════════════════════════════════════════════════════════════

\* CRITICAL: A reader in the Reading state always reads from a ready chunk.
\* This ensures no stale/zero data from unwritten sparse file regions.
NoStaleRead ==
    \A r \in Readers :
        rpc[r] = "Reading" => chunkState[rTarget[r]] = "ready"

\* Once a chunk becomes ready, it stays ready (monotonic progress).
\* Models the fact that MarkChunkReady is write-once.
MonotonicChunkProgress ==
    \A i \in ChunkIndices :
        chunkState[i] = "ready" =>
            chunkState'[i] = "ready" \/ UNCHANGED chunkState

\* Writer processes chunks in order (sequential extraction).
WriterMonotonic ==
    wpc = "Writing" => \A i \in 0..(wIdx-1) : chunkState[i] = "ready"

\* Symmetry for model checking optimization
ReaderSymmetry == Permutations(Readers)

\* ═══════════════════════════════════════════════════════════════════════
\*  LIVENESS PROPERTIES (with FairSpec)
\* ═══════════════════════════════════════════════════════════════════════

\* If the writer completes, all readers targeting completed chunks finish.
AllReadersComplete ==
    (wpc = "Done") ~> (\A r \in Readers : rpc[r] = "Done")

\* Writer eventually completes or fails.
WriterTerminates ==
    <>(wpc \in {"Done", "Failed"})

===========================================================================
