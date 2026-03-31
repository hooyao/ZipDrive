namespace ZipDrive.Domain.Models;

/// <summary>
/// Result of extracting a single archive entry.
/// Wraps the decompressed stream alongside metadata and resource cleanup.
///
/// This type is a plain DTO — NOT IAsyncDisposable. CacheFactoryResult owns
/// stream disposal. ExtractionResult.OnDisposed only cleans up format-specific
/// resources (file handles, archive instances) that outlive the stream.
/// This prevents double-dispose of the stream.
/// </summary>
public sealed class ExtractionResult
{
    /// <summary>Decompressed data stream.</summary>
    public required Stream Stream { get; init; }

    /// <summary>Uncompressed size in bytes (used for cache tier routing).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Cleanup callback for format-specific resources (file handles, archive instances).
    /// Called by the consuming code (FileContentCache) via CacheFactoryResult.OnDisposed
    /// AFTER the stream has been consumed and disposed by the storage strategy.
    /// Null if no cleanup is needed (e.g., the stream is a self-contained MemoryStream).
    /// </summary>
    public Func<ValueTask>? OnDisposed { get; init; }
}
