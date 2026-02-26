namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Result from cache factory, containing the cached value and metadata.
/// The factory is responsible for preparing the data and reporting its size.
/// Implements <see cref="IAsyncDisposable"/> to allow storage strategies to
/// dispose the value and chain resource cleanup via <see cref="OnDisposed"/>.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public sealed class CacheFactoryResult<T> : IAsyncDisposable
{
    private int _disposed;

    /// <summary>
    /// The cached value to store.
    /// </summary>
    public required T Value { get; init; }

    /// <summary>
    /// Size in bytes (for capacity tracking and tier routing).
    /// Discovered by the factory during data preparation.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Optional metadata (e.g., content type, compression ratio, original filename).
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Optional callback invoked after <see cref="Value"/> is disposed.
    /// Use to chain cleanup of owning resources (e.g., dispose an IZipReader
    /// after the decompressed stream has been consumed by the storage strategy).
    /// </summary>
    public Func<ValueTask>? OnDisposed { get; init; }

    /// <summary>
    /// Disposes <see cref="Value"/> (if disposable) then invokes <see cref="OnDisposed"/> (if set).
    /// Safe to call multiple times; only the first call has effect.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        if (Value is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (Value is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (OnDisposed is not null)
        {
            await OnDisposed().ConfigureAwait(false);
        }
    }
}
