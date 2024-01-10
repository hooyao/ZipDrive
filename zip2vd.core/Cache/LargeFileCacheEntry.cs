using System.IO.MemoryMappedFiles;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Cache;

public class LargeFileCacheEntry : IDisposable
{
    private readonly ILogger<LargeFileCacheEntry> _logger;

    public LargeFileCacheEntry(
        string tempFilePath,
        MemoryMappedFile memoryMappedFile,
        MemoryMappedViewAccessor memoryMappedViewAccessor,
        ILogger<LargeFileCacheEntry> logger)
    {
        _logger = logger;
        TempFilePath = tempFilePath;
        MemoryMappedFile = memoryMappedFile;
        MemoryMappedViewAccessor = memoryMappedViewAccessor;
    }

    public string TempFilePath { get; }

    public MemoryMappedFile MemoryMappedFile { get; }

    public MemoryMappedViewAccessor MemoryMappedViewAccessor { get; }

    public void Dispose()
    {
        MemoryMappedViewAccessor.Dispose();
        MemoryMappedFile.Dispose();
        try
        {
            File.Delete(TempFilePath);
        }
        catch (Exception e)
        {
            this._logger.LogError(e, "Failed to delete temp file {TempFilePath}, you may need to delete it manually",
                TempFilePath);
        }
    }
}