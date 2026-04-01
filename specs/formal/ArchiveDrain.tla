--------------------------- MODULE ArchiveDrain ---------------------------
(*
 * Formal model of ZipDrive ArchiveNode drain protocol.
 *
 * Models the interaction between:
 *   - Worker threads calling TryEnter / Exit (Dokan file system callbacks)
 *   - A single drainer calling DrainAsync (archive removal during reload)
 *
 * The TryEnter method uses a double-check pattern:
 *   1. Fast check: if _draining → reject immediately
 *   2. Interlocked.Increment(_activeOps)
 *   3. Double-check: if _draining → decrement, (signal if last), reject
 *   4. Accepted: worker is now active
 *
 * This prevents new operations from entering after drain starts, while
 * allowing in-flight operations to complete gracefully.
 *
 * C# source: src/ZipDrive.Application/Services/ArchiveNode.cs
 *)
EXTENDS Integers, FiniteSets, TLC

CONSTANTS
    Workers         \* Set of worker thread IDs (e.g., {w1, w2, w3})

ASSUME Workers # {}

VARIABLES
    \* ══════ Shared State (ArchiveNode fields) ══════
    draining,       \* BOOLEAN — volatile bool _draining
    activeOps,      \* Nat     — int _activeOps (Interlocked)
    drainTcsDone,   \* BOOLEAN — TaskCompletionSource signaled?

    \* ══════ Per-Worker State ══════
    wpc,            \* [Workers -> STRING] — program counter

    \* ══════ Drainer State ══════
    dpc             \* STRING — drainer program counter

allVars == <<draining, activeOps, drainTcsDone, wpc, dpc>>

\* ─── State Enums ────────────────────────────────────────────────────────

WorkerStates == { "Idle",
                  "CheckDrain1",    \* Fast rejection check
                  "IncOps",         \* Interlocked.Increment
                  "CheckDrain2",    \* Double-check after increment
                  "Active",         \* In-flight operation
                  "DecExit",        \* Exit: Interlocked.Decrement
                  "Rejected",       \* TryEnter returned false
                  "Done" }          \* Worker completed

DrainerStates == { "Idle",
                   "CreateTCS",     \* Initialize TaskCompletionSource
                   "SetDraining",   \* volatile write: _draining = true
                   "CancelToken",   \* Cancel _drainCts
                   "CheckOps",      \* Check activeOps == 0
                   "WaitTCS",       \* Await drain completion
                   "Done" }

\* ─── Type Invariant ─────────────────────────────────────────────────────

TypeOK ==
    /\ draining      \in BOOLEAN
    /\ activeOps     \in Int       \* Int (not Nat) to detect underflow bugs
    /\ drainTcsDone  \in BOOLEAN
    /\ wpc           \in [Workers -> WorkerStates]
    /\ dpc           \in DrainerStates

\* ─── Initial State ──────────────────────────────────────────────────────

Init ==
    /\ draining      = FALSE
    /\ activeOps     = 0
    /\ drainTcsDone  = FALSE
    /\ wpc           = [w \in Workers |-> "Idle"]
    /\ dpc           = "Idle"

\* ═══════════════════════════════════════════════════════════════════════
\*  WORKER ACTIONS (models TryEnter + operation + Exit)
\*  ArchiveNode.cs:54-77
\* ═══════════════════════════════════════════════════════════════════════

\* Worker decides to start an operation
WStart(w) ==
    /\ wpc[w] = "Idle"
    /\ wpc' = [wpc EXCEPT ![w] = "CheckDrain1"]
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, dpc>>

\* TryEnter step 1: Fast rejection check (volatile read)
\* ArchiveNode.cs:56
WCheckDrain1(w) ==
    /\ wpc[w] = "CheckDrain1"
    /\ IF draining
       THEN wpc' = [wpc EXCEPT ![w] = "Rejected"]
       ELSE wpc' = [wpc EXCEPT ![w] = "IncOps"]
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, dpc>>

\* TryEnter step 2: Interlocked.Increment(_activeOps)
\* ArchiveNode.cs:57
WIncOps(w) ==
    /\ wpc[w] = "IncOps"
    /\ activeOps' = activeOps + 1
    /\ wpc' = [wpc EXCEPT ![w] = "CheckDrain2"]
    /\ UNCHANGED <<draining, drainTcsDone, dpc>>

\* TryEnter step 3: Double-check after increment (volatile read)
\* ArchiveNode.cs:58-63
\* If draining was set between CheckDrain1 and IncOps, we catch it here.
WCheckDrain2(w) ==
    /\ wpc[w] = "CheckDrain2"
    /\ IF draining
       THEN \* Reject: decrement and potentially signal drain completion
            /\ activeOps' = activeOps - 1
            /\ drainTcsDone' =
                IF activeOps - 1 = 0 THEN TRUE ELSE drainTcsDone
            /\ wpc' = [wpc EXCEPT ![w] = "Rejected"]
       ELSE \* Accepted: worker is now active
            /\ wpc' = [wpc EXCEPT ![w] = "Active"]
            /\ UNCHANGED <<activeOps, drainTcsDone>>
    /\ UNCHANGED <<draining, dpc>>

\* Worker performs its operation (non-deterministic duration)
\* This models the time between TryEnter returning true and Exit being called.
WActive(w) ==
    /\ wpc[w] = "Active"
    /\ wpc' = [wpc EXCEPT ![w] = "DecExit"]
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, dpc>>

\* Exit: Interlocked.Decrement(_activeOps) + signal if last
\* ArchiveNode.cs:71-77
WDecExit(w) ==
    /\ wpc[w] = "DecExit"
    /\ activeOps' = activeOps - 1
    /\ drainTcsDone' =
        IF activeOps - 1 = 0 /\ draining THEN TRUE ELSE drainTcsDone
    /\ wpc' = [wpc EXCEPT ![w] = "Done"]
    /\ UNCHANGED <<draining, dpc>>

\* Rejected workers return to Idle (can retry)
WRejectedRetry(w) ==
    /\ wpc[w] = "Rejected"
    /\ wpc' = [wpc EXCEPT ![w] = "Done"]
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, dpc>>

\* ═══════════════════════════════════════════════════════════════════════
\*  DRAINER ACTIONS (models DrainAsync)
\*  ArchiveNode.cs:84-115
\*  Single-caller assumption (called from RemoveArchiveAsync)
\* ═══════════════════════════════════════════════════════════════════════

\* Drainer initiates drain
DStart ==
    /\ dpc = "Idle"
    /\ dpc' = "CreateTCS"
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, wpc>>

\* Create TaskCompletionSource (before setting _draining)
\* ArchiveNode.cs:101
DCreateTCS ==
    /\ dpc = "CreateTCS"
    /\ drainTcsDone' = FALSE
    /\ dpc' = "SetDraining"
    /\ UNCHANGED <<draining, activeOps, wpc>>

\* Set _draining = true (volatile write acts as release fence)
\* ArchiveNode.cs:102
\* NOTE: TCS must be created BEFORE this, because the volatile write
\* provides a release fence ensuring the TCS store is visible to any
\* thread that observes _draining == true.
DSetDraining ==
    /\ dpc = "SetDraining"
    /\ draining' = TRUE
    /\ dpc' = "CancelToken"
    /\ UNCHANGED <<activeOps, drainTcsDone, wpc>>

\* Cancel drain token (aborts in-flight prefetch)
\* ArchiveNode.cs:105  _drainCts?.Cancel()
\* Modeled as no-op (prefetch is out of scope for this model)
DCancelToken ==
    /\ dpc = "CancelToken"
    /\ dpc' = "CheckOps"
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, wpc>>

\* Check if activeOps == 0 (Volatile.Read)
\* ArchiveNode.cs:107-108
\* If no operations are active, signal drain completion immediately.
DCheckOps ==
    /\ dpc = "CheckOps"
    /\ IF activeOps = 0
       THEN /\ drainTcsDone' = TRUE
            /\ dpc' = "Done"
       ELSE /\ dpc' = "WaitTCS"
            /\ UNCHANGED drainTcsDone
    /\ UNCHANGED <<draining, activeOps, wpc>>

\* Wait for drain TCS to be signaled (await _drainTcs.Task)
\* ArchiveNode.cs:113
DWaitTCS ==
    /\ dpc = "WaitTCS"
    /\ drainTcsDone       \* blocks until TCS.TrySetResult
    /\ dpc' = "Done"
    /\ UNCHANGED <<draining, activeOps, drainTcsDone, wpc>>

\* ═══════════════════════════════════════════════════════════════════════
\*  SPECIFICATION
\* ═══════════════════════════════════════════════════════════════════════

WorkerAction(w) ==
    \/ WStart(w)
    \/ WCheckDrain1(w)
    \/ WIncOps(w)
    \/ WCheckDrain2(w)
    \/ WActive(w)
    \/ WDecExit(w)
    \/ WRejectedRetry(w)

DrainerAction ==
    \/ DStart
    \/ DCreateTCS
    \/ DSetDraining
    \/ DCancelToken
    \/ DCheckOps
    \/ DWaitTCS

\* System quiescence: drainer done, all workers done → stutter.
Terminated ==
    /\ dpc = "Done"
    /\ \A w \in Workers : wpc[w] = "Done"
    /\ UNCHANGED allVars

Next ==
    \/ \E w \in Workers : WorkerAction(w)
    \/ DrainerAction
    \/ Terminated

Spec     == Init /\ [][Next]_allVars
FairSpec == Spec /\ WF_allVars(Next)

\* ═══════════════════════════════════════════════════════════════════════
\*  SAFETY PROPERTIES
\* ═══════════════════════════════════════════════════════════════════════

\* CRITICAL: After drain completes, no worker is in Active state.
\* This ensures the archive can be safely removed after DrainAsync returns.
NoDrainedActive ==
    drainTcsDone => \A w \in Workers : wpc[w] # "Active"

\* activeOps is non-negative (no underflow from unbalanced TryEnter/Exit)
ActiveOpsNonNeg ==
    activeOps >= 0

\* When drain is done and no worker is mid-TryEnter, activeOps is 0.
\* Workers transiently in IncOps (between CheckDrain1 and CheckDrain2) may
\* have activeOps > 0 even after drain — they decrement upon rejection.
DrainImpliesZeroOps ==
    (dpc = "Done" /\ drainTcsDone
     /\ \A w \in Workers : wpc[w] \notin {"IncOps", "CheckDrain2"})
    => activeOps = 0

\* After drain completes, draining flag remains true (no re-entry)
DrainFlagPersists ==
    drainTcsDone => draining

\* Symmetry for model checking optimization
WorkerSymmetry == Permutations(Workers)

\* ═══════════════════════════════════════════════════════════════════════
\*  LIVENESS PROPERTIES (with FairSpec)
\* ═══════════════════════════════════════════════════════════════════════

\* If drain starts, it eventually completes.
DrainCompletes ==
    (dpc = "SetDraining") ~> (dpc = "Done")

\* All workers eventually terminate (reach Done or Rejected)
AllWorkersTerminate ==
    \A w \in Workers : <>(wpc[w] = "Done")

===========================================================================
