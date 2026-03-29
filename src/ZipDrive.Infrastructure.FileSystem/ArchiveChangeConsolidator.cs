using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Queues FileSystemWatcher events and consolidates them into net deltas.
/// Fires a callback after a quiet period with the consolidated changeset.
/// Uses Interlocked.Exchange for atomic flush — zero event loss.
/// </summary>
internal sealed class ArchiveChangeConsolidator : IAsyncDisposable
{
    private ConcurrentDictionary<string, ChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<ArchiveChangeDelta, Task> _onFlush;
    private readonly ILogger _logger;
    private readonly TimeSpan _quietPeriod;
    private readonly Timer _timer;
    private long _lastEventTicks;
    private volatile bool _disposed;
    private volatile Task? _inflightFlush;

    public ArchiveChangeConsolidator(
        TimeSpan quietPeriod,
        Func<ArchiveChangeDelta, Task> onFlush,
        ILogger logger)
    {
        _quietPeriod = quietPeriod;
        _onFlush = onFlush ?? throw new ArgumentNullException(nameof(onFlush));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void OnCreated(string relativePath)
    {
        if (_disposed) return;
        _pending.AddOrUpdate(relativePath, ChangeKind.Added, (_, prev) => prev switch
        {
            ChangeKind.Removed => ChangeKind.Modified,
            ChangeKind.Noop => ChangeKind.Added,
            _ => ChangeKind.Added
        });
        ResetTimer();
    }

    public void OnDeleted(string relativePath)
    {
        if (_disposed) return;
        _pending.AddOrUpdate(relativePath, ChangeKind.Removed, (_, prev) => prev switch
        {
            ChangeKind.Added => ChangeKind.Noop,
            ChangeKind.Modified => ChangeKind.Removed,
            ChangeKind.Noop => ChangeKind.Removed,
            _ => ChangeKind.Removed
        });
        ResetTimer();
    }

    public void OnRenamed(string oldRelativePath, string newRelativePath)
    {
        OnDeleted(oldRelativePath);
        OnCreated(newRelativePath);
    }

    /// <summary>
    /// Atomically discards all pending events.
    /// Call before full reconciliation to prevent stale events from re-applying.
    /// </summary>
    public void ClearPending()
    {
        Interlocked.Exchange(ref _pending, new ConcurrentDictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Forces an immediate flush of pending events (bypasses timer).
    /// Used for testing and watcher buffer overflow recovery.
    /// </summary>
    public Task ForceFlushAsync() => FlushAsync();

    private void ResetTimer()
    {
        Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);
        try
        {
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Timer already disposed during shutdown
        }
    }

    private void OnTimerTick(object? state)
    {
        if (_disposed) return;

        long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastEventTicks);
        if (elapsed < _quietPeriod.TotalMilliseconds)
        {
            var remaining = TimeSpan.FromMilliseconds(_quietPeriod.TotalMilliseconds - elapsed);
            try
            {
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
            return;
        }

        // Assign before starting so DisposeAsync can observe it immediately
        var flushTask = FlushAsync();
        _inflightFlush = flushTask;
        _ = flushTask.ContinueWith(
            t => _logger.LogError(t.Exception, "Unhandled exception in consolidator flush"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task FlushAsync()
    {
        // Atomic swap — events arriving during flush processing land in the new dictionary
        var snapshot = Interlocked.Exchange(
            ref _pending,
            new ConcurrentDictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase));

        var added = snapshot.Where(x => x.Value == ChangeKind.Added).Select(x => x.Key).ToList();
        var removed = snapshot.Where(x => x.Value == ChangeKind.Removed).Select(x => x.Key).ToList();
        var modified = snapshot.Where(x => x.Value == ChangeKind.Modified).Select(x => x.Key).ToList();

        if (added.Count == 0 && removed.Count == 0 && modified.Count == 0)
            return;

        _logger.LogInformation(
            "Archive changes consolidated: +{Added} -{Removed} ~{Modified}",
            added.Count, removed.Count, modified.Count);

        try
        {
            await _onFlush(new ArchiveChangeDelta(added, removed, modified));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying archive change delta");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _timer.DisposeAsync();

        // Await any in-flight flush to prevent _onFlush from running after caller tears down state
        var flush = _inflightFlush;
        if (flush != null)
        {
            try { await flush; }
            catch { /* already logged by ContinueWith */ }
        }
    }
}

internal enum ChangeKind { Added, Removed, Modified, Noop }

internal sealed record ArchiveChangeDelta(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Modified);
