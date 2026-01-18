using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Stores file content as memory-mapped file on disk.
/// Use for large files (≥ 50MB by default).
/// Internal storage: DiskCacheEntry (temp file path + MMF)
/// Returns: Stream (MMF view stream)
/// </summary>
public sealed class DiskStorageStrategy : IStorageStrategy<Stream>
{
    private readonly string _tempDirectory;
    private readonly ILogger<DiskStorageStrategy>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiskStorageStrategy"/> class.
    /// </summary>
    /// <param name="tempDirectory">The directory for temporary cache files. If null, uses system temp.</param>
    /// <param name="logger">Optional logger instance.</param>
    public DiskStorageStrategy(string? tempDirectory = null, ILogger<DiskStorageStrategy>? logger = null)
    {
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
        _logger = logger;

        // Ensure temp directory exists
        if (!Directory.Exists(_tempDirectory))
        {
            Directory.CreateDirectory(_tempDirectory);
        }
    }

    /// <inheritdoc />
    public async Task<StoredEntry> StoreAsync(CacheFactoryResult<Stream> result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Value);

        string tempPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}.zip2vd.cache");

        _logger?.LogDebug("Creating temp file: {Path} ({Size} bytes)", tempPath, result.SizeBytes);

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
            _logger?.LogWarning(ex, "Failed to dispose MMF for {Path}", entry.TempFilePath);
        }

        try
        {
            if (File.Exists(entry.TempFilePath))
            {
                File.Delete(entry.TempFilePath);
                _logger?.LogDebug("Deleted temp file: {Path}", entry.TempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete temp file: {Path}", entry.TempFilePath);
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
