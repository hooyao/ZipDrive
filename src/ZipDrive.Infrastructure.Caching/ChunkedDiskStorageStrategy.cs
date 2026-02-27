using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        ChunkedFileEntry? entry = null;
        bool extractionStarted = false;

        try
        {
            // Call factory to get the decompressed stream
            result = await factory(cancellationToken).ConfigureAwait(false);
            long sizeBytes = result.SizeBytes;

            _logger.LogInformation(
                "ChunkedDiskStorageStrategy: Starting chunked extraction ({SizeMb:F1} MB, chunk={ChunkMb:F1} MB)",
                sizeBytes / (1024.0 * 1024.0),
                _chunkSizeBytes / (1024.0 * 1024.0));

            // Create sparse file sized to full uncompressed size.
            // Must explicitly set the NTFS sparse attribute via FSCTL_SET_SPARSE
            // before calling SetLength, otherwise the OS pre-allocates the full size on disk.
            await using (FileStream createFs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                if (sizeBytes > 0)
                {
                    SetSparseAttribute(createFs);
                    createFs.SetLength(sizeBytes);
                }
            }

            // Create the chunk orchestrator
            entry = new ChunkedFileEntry(tempPath, sizeBytes, _chunkSizeBytes);

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
            extractionStarted = true; // ExtractAsync will dispose the stream in its finally block

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
            if (extractionStarted && entry is not null)
            {
                // Extraction task is running — cancel it, wait for it to finish
                // (which disposes the decompressed stream + ZipReader), then dispose the entry
                // (which deletes the backing file and cancels pending chunk waiters).
                try
                {
                    entry.ExtractionCts.Cancel();
                    await entry.ExtractionTask.ConfigureAwait(false);
                }
                catch { /* extraction task may have faulted or been cancelled */ }
                finally
                {
                    entry.Dispose();
                }
            }
            else
            {
                // Extraction not started — dispose factory result directly
                if (result is not null)
                {
                    try { await result.DisposeAsync().ConfigureAwait(false); }
                    catch { /* best-effort cleanup */ }
                }

                entry?.Dispose();

                // Clean up partial temp file
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

    /// <summary>
    /// Sets the NTFS sparse attribute on a file via FSCTL_SET_SPARSE.
    /// Without this, SetLength pre-allocates the full file size on disk.
    /// Falls back silently on non-NTFS volumes (sparse not supported).
    /// </summary>
    private void SetSparseAttribute(FileStream fs)
    {
        try
        {
            int bytesReturned = 0;
            bool success = DeviceIoControl(
                fs.SafeFileHandle,
                FSCTL_SET_SPARSE,
                IntPtr.Zero, 0,
                IntPtr.Zero, 0,
                ref bytesReturned,
                IntPtr.Zero);

            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                _logger.LogDebug(
                    "FSCTL_SET_SPARSE failed (error {Error}) — file system may not support sparse files, falling back to regular file",
                    error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set sparse attribute, falling back to regular file");
        }
    }

    private const int FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        ref int lpBytesReturned,
        IntPtr lpOverlapped);
}
