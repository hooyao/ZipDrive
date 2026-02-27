using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as an NTFS sparse file on disk with incremental chunk-based extraction.
/// Replaces the former DiskStorageStrategy — all disk-tier files benefit from first-byte latency
/// improvement via chunked extraction.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaterializeAsync"/> returns after the first chunk is extracted (~50ms for 10MB),
/// while a background task continues extracting remaining chunks. Each call to <see cref="Retrieve"/>
/// returns a fresh <see cref="ChunkedStream"/> that blocks on unextracted regions.
/// </para>
/// <para>
/// Internal storage: <see cref="ChunkedFileEntry"/> (sparse file + chunk state tracking).
/// Returns: <see cref="Stream"/> (<see cref="ChunkedStream"/> wrapping the sparse file).
/// </para>
/// </remarks>
public sealed class ChunkedDiskStorageStrategy : IStorageStrategy<Stream>
{
    private readonly string _tempDirectory;
    private readonly int _chunkSizeBytes;
    private readonly ILogger<ChunkedDiskStorageStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChunkedDiskStorageStrategy"/> class.
    /// Creates a per-process subdirectory under the base temp directory.
    /// </summary>
    public ChunkedDiskStorageStrategy(
        ILogger<ChunkedDiskStorageStrategy> logger,
        int chunkSizeBytes,
        string? tempDirectory = null)
    {
        _logger = logger;
        _chunkSizeBytes = chunkSizeBytes > 0
            ? chunkSizeBytes
            : throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "Chunk size must be positive.");

        string baseDir = tempDirectory ?? Path.GetTempPath();
        _tempDirectory = Path.Combine(baseDir, $"ZipDrive-{Environment.ProcessId}");

        if (!Directory.Exists(_tempDirectory))
        {
            Directory.CreateDirectory(_tempDirectory);
            _logger.LogInformation("Created cache directory: {Path}", _tempDirectory);
        }
    }

    /// <inheritdoc />
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        string tempPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}.zip2vd.chunked");

        CacheFactoryResult<Stream>? result = null;
        bool extractionOwnsResult = false;

        try
        {
            // Call factory to get the decompressed stream
            result = await factory(cancellationToken).ConfigureAwait(false);
            long sizeBytes = result.SizeBytes;

            _logger.LogInformation(
                "ChunkedDiskStorageStrategy: Starting chunked extraction ({SizeMb:F1} MB, chunk={ChunkMb:F1} MB)",
                sizeBytes / (1024.0 * 1024.0),
                _chunkSizeBytes / (1024.0 * 1024.0));

            // Create sparse file sized to full uncompressed size
            await using (FileStream createFs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                if (sizeBytes > 0)
                    createFs.SetLength(sizeBytes);
            }

            // Create the chunk orchestrator
            ChunkedFileEntry entry = new ChunkedFileEntry(tempPath, sizeBytes, _chunkSizeBytes);

            if (entry.ChunkCount == 0)
            {
                // Empty file — nothing to extract, dispose factory resources
                await result.DisposeAsync().ConfigureAwait(false);
                result = null; // Prevent double-dispose in catch

                return new StoredEntry(entry, sizeBytes);
            }

            // Start background extraction (fire-and-forget).
            // The extraction task owns the decompressed stream and OnDisposed callback lifecycle.
            long startTimestamp = Stopwatch.GetTimestamp();
            CancellationToken extractionToken = entry.ExtractionCts.Token;
            extractionOwnsResult = true; // ExtractAsync will dispose the stream in its finally block

            entry.ExtractionTask = Task.Run(async () =>
            {
                await entry.ExtractAsync(
                    result.Value,
                    result.OnDisposed,
                    _logger,
                    extractionToken).ConfigureAwait(false);

                double elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                double throughputMbps = elapsedMs > 0 ? sizeBytes / (1024.0 * 1024.0) / (elapsedMs / 1000.0) : 0;

                _logger.LogInformation(
                    "ChunkedDiskStorageStrategy: Extraction complete ({SizeMb:F1} MB, {ElapsedMs:F0} ms, {ThroughputMbps:F0} MB/s)",
                    sizeBytes / (1024.0 * 1024.0),
                    elapsedMs,
                    throughputMbps);
            }, CancellationToken.None); // Use None so the Task.Run itself isn't cancelled

            // Await first chunk — this is the first-byte latency
            await entry.WaitForChunkAsync(0, cancellationToken).ConfigureAwait(false);

            return new StoredEntry(entry, sizeBytes);
        }
        catch
        {
            // Dispose factory result if the extraction task hasn't taken ownership
            if (!extractionOwnsResult && result is not null)
            {
                try { await result.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }

            // Clean up partial temp file on failure
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    _logger.LogDebug("Cleaned up partial temp file: {Path}", tempPath);
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up partial temp file: {Path}", tempPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        return new ChunkedStream(entry);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ChunkedFileEntry entry = (ChunkedFileEntry)stored.Data;
        entry.Dispose();
    }

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => true;

    /// <summary>
    /// Deletes the entire cache directory (including any remaining files).
    /// Call after <see cref="GenericCache{T}.Clear"/> on shutdown.
    /// </summary>
    public void DeleteCacheDirectory()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
                _logger.LogInformation("Deleted cache directory: {Path}", _tempDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cache directory: {Path}", _tempDirectory);
        }
    }
}
