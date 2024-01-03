using System.IO.MemoryMappedFiles;

namespace zip2vd.core.Common;

public class LargeFileCacheEntry
{
    public LargeFileCacheEntry(string tempFilePath, MemoryMappedFile memoryMappedFile, MemoryMappedViewAccessor memoryMappedViewAccessor)
    {
        TempFilePath = tempFilePath;
        MemoryMappedFile = memoryMappedFile;
        MemoryMappedViewAccessor = memoryMappedViewAccessor;
    }
    public string TempFilePath { get; }

    public MemoryMappedFile MemoryMappedFile { get; }
    
    public MemoryMappedViewAccessor MemoryMappedViewAccessor { get;}
}