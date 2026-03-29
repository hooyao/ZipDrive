using System.Diagnostics;
using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Tracks in-flight operations for a single archive.
/// Used by VFS to drain operations before removal.
/// NOTE: DrainAsync is expected to be called by a single caller (RemoveArchiveAsync
/// via ApplyDeltaAsync). The double-drain guard handles accidental re-entry but
/// concurrent DrainAsync calls from different threads are not supported.
/// </summary>
internal sealed class ArchiveNode : IDisposable
{
    public ArchiveDescriptor Descriptor { get; }

    private int _activeOps;
    private volatile bool _draining;
    private TaskCompletionSource? _drainTcs;
    private CancellationTokenSource? _drainCts;
    private int _disposed; // 0 = active, 1 = disposed (Interlocked for thread safety)

    public ArchiveNode(ArchiveDescriptor descriptor) =>
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    public bool IsDraining => _draining;
    public int ActiveOps => Volatile.Read(ref _activeOps);

    /// <summary>
    /// Cancellation token that is cancelled when drain starts.
    /// Pass this to fire-and-forget operations (e.g., prefetch) so they abort promptly.
    /// </summary>
    public CancellationToken DrainToken
    {
        get
        {
            // Thread-safe lazy init — exactly one CTS is published
            if (_drainCts != null) return _drainCts.Token;
            var newCts = new CancellationTokenSource();
            var existing = Interlocked.CompareExchange(ref _drainCts, newCts, null);
            if (existing != null)
            {
                newCts.Dispose();
                return existing.Token;
            }
            return newCts.Token;
        }
    }

    /// <summary>
    /// Attempts to enter this archive for an operation.
    /// Returns false if archive is draining (caller should return FileNotFound).
    /// </summary>
    public bool TryEnter()
    {
        if (_draining) return false;           // Fast rejection
        Interlocked.Increment(ref _activeOps);
        if (_draining)                         // Double-check after increment
        {
            if (Interlocked.Decrement(ref _activeOps) == 0)
                _drainTcs?.TrySetResult();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Exits this archive after an operation completes.
    /// Signals drain completion when last operation exits.
    /// </summary>
    public void Exit()
    {
        int newCount = Interlocked.Decrement(ref _activeOps);
        Debug.Assert(newCount >= 0, $"ArchiveNode.Exit called without matching TryEnter (ActiveOps went to {newCount})");
        if (newCount == 0 && _draining)
            _drainTcs?.TrySetResult();
    }

    /// <summary>
    /// Initiates drain: no new operations accepted.
    /// Returns a task that completes when all in-flight operations finish (or timeout).
    /// Single-caller assumption: called only from RemoveArchiveAsync (serialized by ApplyDeltaAsync).
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        // Guard against double-drain: if already draining, await existing drain
        if (_draining)
        {
            if (_drainTcs != null && timeout > TimeSpan.Zero)
            {
                using var cts2 = new CancellationTokenSource(timeout);
                try { await _drainTcs.Task.WaitAsync(cts2.Token); }
                catch (OperationCanceledException) { }
            }
            return;
        }

        // Create TCS before setting _draining — volatile write to _draining provides
        // a release fence, ensuring the prior store to _drainTcs is visible to any
        // thread that observes _draining == true.
        _drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _draining = true;

        // Cancel per-archive token (aborts in-flight prefetch for this archive)
        _drainCts?.Cancel();

        if (Volatile.Read(ref _activeOps) == 0)
            _drainTcs.TrySetResult();   // Already drained

        if (timeout <= TimeSpan.Zero) return;

        using var cts = new CancellationTokenSource(timeout);
        try { await _drainTcs.Task.WaitAsync(cts.Token); }
        catch (OperationCanceledException) { /* timeout — proceed anyway */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _drainCts?.Dispose();
    }
}
