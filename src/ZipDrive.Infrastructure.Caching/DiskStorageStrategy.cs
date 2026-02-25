using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Stores file content as memory-mapped file on disk.
/// Use for large files (≥ 50MB by default).
/// Internal storage: DiskCacheEntry (temp file path + MMF)
/// Returns: Stream (MMF view stream)
/// </summary>
public sealed class DiskStorageStrategy : IStorageStrategy<Stream>
{
    private readonly string _tempDirectory;
    private readonly ILogger<DiskStorageStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiskStorageStrategy"/> class.
    /// Creates a per-process subdirectory (ZipDrive-{pid}) under the base temp directory,
    /// ensuring multiple concurrent ZipDrive processes have isolated disk caches.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tempDirectory">The base directory for temporary cache files. If null, uses system temp.</param>
    public DiskStorageStrategy(ILogger<DiskStorageStrategy> logger, string? tempDirectory = null)
    {
        _logger = logger;

        string baseDir = tempDirectory ?? Path.GetTempPath();
        _tempDirectory = Path.Combine(baseDir, $"ZipDrive-{Environment.ProcessId}");

        // Ensure temp directory exists (creates base + subdirectory as needed)
        if (!Directory.Exists(_tempDirectory))
        {
            Directory.CreateDirectory(_tempDirectory);
            _logger.LogInformation("Created cache directory: {Path}", _tempDirectory);
        }
    }

    /// <inheritdoc />
    public async Task<StoredEntry> StoreAsync(CacheFactoryResult<Stream> result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Value);

        string tempPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}.zip2vd.cache");

        _logger.LogDebug("Creating temp file: {Path} ({Size} bytes)", tempPath, result.SizeBytes);

        // Write to temp file
        await using (FileStream fileStream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await result.Value.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Create memory-mapped file for random access
        MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            tempPath,
            FileMode.Open,
            mapName: null,
            result.SizeBytes,
            MemoryMappedFileAccess.Read);

        DiskCacheEntry entry = new DiskCacheEntry(tempPath, mmf, result.SizeBytes);
        return new StoredEntry(entry, result.SizeBytes);
    }

    /// <inheritdoc />
    public Stream Retrieve(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        DiskCacheEntry entry = (DiskCacheEntry)stored.Data;
        return entry.MemoryMappedFile.CreateViewStream(0, entry.Size, MemoryMappedFileAccess.Read);
    }

    /// <inheritdoc />
    public void Dispose(StoredEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        DiskCacheEntry entry = (DiskCacheEntry)stored.Data;

        try
        {
            entry.MemoryMappedFile.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose MMF for {Path}", entry.TempFilePath);
        }

        try
        {
            if (File.Exists(entry.TempFilePath))
            {
                File.Delete(entry.TempFilePath);
                _logger.LogDebug("Deleted temp file: {Path}", entry.TempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp file: {Path}", entry.TempFilePath);
        }
    }

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

    /// <inheritdoc />
    public bool RequiresAsyncCleanup => true; // File deletion can be slow
}

/// <summary>
/// Internal storage representation for disk-cached files.
/// Not exposed to cache users.
/// </summary>
internal sealed record DiskCacheEntry(
    string TempFilePath,
    MemoryMappedFile MemoryMappedFile,
    long Size);
