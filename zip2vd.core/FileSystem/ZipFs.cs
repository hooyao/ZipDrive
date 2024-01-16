using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Text;
using DokanNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using zip2vd.core.Cache;
using zip2vd.core.Common;
using zip2vd.core.Configuration;
using zip2vd.core.Proxy;
using FileAccess = DokanNet.FileAccess;

namespace zip2vd.core.FileSystem;

public class ZipFs : IDokanOperations, IDisposable
{
    private readonly ArchiveFileSystemOptions _fsOptions;
    private readonly string _filePath;
    private readonly FileInfo _zipFileInfo;

    private readonly EntryNode<ZipEntryAttr> _root;
    private volatile bool _buildTree = false;

    private readonly object _zipFileLock = new object();

    private LruMemoryCache<string, byte[]> _smallFileCache;
    private LruMemoryCache<string, LargeFileCacheEntry> _largeFileCache;

    private ILogger<ZipFs> _logger;
    private ILogger<LargeFileCacheEntry> _largeFileCacheEntryLogger;

    private ObjectPool<VerboseZipArchive> _zipArchivePool;

    //Configuration Variables
    private readonly string _largeFileCacheDir;
    private readonly long _smallFileSizeCutOff;
    private readonly int _maxReadConcurrency;
    private readonly long _smallFileCacheSize;
    private readonly long _largeFileCacheSize;

    public ZipFs(string filePath, ArchiveFileSystemOptions fsOptions, ILoggerFactory loggerFactory)
    {
        this._logger = loggerFactory.CreateLogger<ZipFs>();
        this._largeFileCacheEntryLogger = loggerFactory.CreateLogger<LargeFileCacheEntry>();
        this._fsOptions = fsOptions;
        this._filePath = filePath;
        this._smallFileSizeCutOff = fsOptions.SmallFileSizeCutoffInMb*1024L*1024L;
        this._maxReadConcurrency = fsOptions.MaxReadConcurrency;
        this._smallFileCacheSize = fsOptions.SmallFileCacheSizeInMb*1024L*1024L;
        this._largeFileCacheSize = fsOptions.LargeFileCacheSizeInMb*1024L*1024L;
        this._largeFileCacheDir =
            string.IsNullOrEmpty(fsOptions.LargeFileCacheDir) ? Path.GetTempPath() : fsOptions.LargeFileCacheDir;
        this._logger.LogInformation("ZipFS configurations: SmallFileSizeCutOff: {SmallFileSizeCutOff}MB, " +
                                     "MaxReadConcurrency: {MaxReadConcurrency}, SmallFileCacheSize: {SmallFileCacheSize}MB, " +
                                     "LargeFileCacheSize: {LargeFileCacheSize}MB, LargeFileCacheDir: {LargeFileCacheDir}",
            fsOptions.SmallFileSizeCutoffInMb, fsOptions.MaxReadConcurrency, fsOptions.SmallFileCacheSizeInMb,
            fsOptions.LargeFileCacheSizeInMb, this._largeFileCacheDir);
        if (!Path.Exists(this._largeFileCacheDir))
        {
            Directory.CreateDirectory(this._largeFileCacheDir);
        }

        this._zipFileInfo = new FileInfo(filePath);
        // Current system non-unicode code page
        Encoding ansiEncoding = Encoding.GetEncoding(0);
        DefaultObjectPoolProvider p = new DefaultObjectPoolProvider() { MaximumRetained = this._maxReadConcurrency };
        this._zipArchivePool = p.Create<VerboseZipArchive>(new ZipArchivePooledObjectPolicy(this._filePath, ansiEncoding, loggerFactory));

        this._smallFileCache = new LruMemoryCache<string, byte[]>(this._smallFileCacheSize, loggerFactory);
        this._largeFileCache =
            new LruMemoryCache<string, LargeFileCacheEntry>(this._largeFileCacheSize, loggerFactory);

        this._root = new EntryNode<ZipEntryAttr>(true, "/", null, new FileInformation()
        {
            Attributes = FileAttributes.Directory,
            CreationTime = this._zipFileInfo.LastWriteTime,
            FileName = "/",
            LastAccessTime = this._zipFileInfo.LastWriteTime,
            LastWriteTime = this._zipFileInfo.LastWriteTime,
            Length = 0L
        });
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options,
        FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        this._logger.LogTrace("Reading file: {FileName}, Offset: {Offset}, BufferSize: {BufferSize}",
            fileName, offset, buffer.Length);
        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var node = this.LocateNode(parts);
            ZipEntryAttr? attr = node.Attributes;
            if (!attr.HasValue)
            {
                throw new FileNotFoundException();
            }


            VerboseZipArchive archive = this._zipArchivePool.Get();
            try
            {
                ZipArchiveEntry? entry = archive.GetEntry(attr.Value.FullPath);
                if (entry == null)
                {
                    throw new FileNotFoundException();
                }

                long fileSize = entry.Length;
                if (fileSize <= this._smallFileSizeCutOff)
                {
                    using (LruMemoryCache<string, byte[]>.CacheItem cacheItem = this._smallFileCache.BorrowOrAdd(
                               fileName,
                               () =>
                               {
                                   this._logger.LogDebug("Caching {LargeOrSmall} file: {FileName}", "small", fileName);
                                   using (Stream entryStream = entry.Open())
                                   using (BufferedStream bs = new BufferedStream(entryStream))
                                   using (MemoryStream ms = new MemoryStream())
                                   {
                                       bs.CopyTo(ms);
                                       return ms.ToArray();
                                   }
                               }, fileSize))
                    {
                        if (cacheItem.CacheItemValue == null)
                        {
                            throw new FileNotFoundException();
                        }

                        // Calculate the number of bytes that can be copied
                        int bytesToCopy = Math.Min(buffer.Length, cacheItem.CacheItemValue.Length - (int)offset);

                        // Copy the bytes
                        cacheItem.CacheItemValue.AsSpan().Slice((int)offset, bytesToCopy).CopyTo(buffer);
                        // using (RecyclableMemoryStream ms = rmsMgr.GetStream(fileBytes))
                        // {
                        //     ms.Seek(offset, SeekOrigin.Begin);
                        //     bytesRead = ms.Read(buffer, 0, buffer.Length);
                        //     return DokanResult.Success;
                        // }
                        bytesRead = bytesToCopy;
                        return DokanResult.Success;
                    }
                }
                else
                {
                    // using (Stream stream = entry.Open())
                    // using (RecyclableMemoryStream ms = rmsMgr.GetStream())
                    // {
                    //     stream.CopyTo(ms);
                    //     ms.Seek(offset, SeekOrigin.Begin);
                    //     bytesRead = ms.Read(buffer, 0, buffer.Length);
                    //     return DokanResult.Success;
                    // }

                    // Open the source file stream
                    using (LruMemoryCache<string, LargeFileCacheEntry>.CacheItem largeFileCacheItem =
                           this._largeFileCache.BorrowOrAdd(fileName, () =>
                               {
                                   this._logger.LogDebug("Start Caching {LargeOrSmall} file: {FileName}", "LARGE", fileName);
                                   string tempFileName = $"{Guid.NewGuid().ToString()}.zip2vd";
                                   using (Stream entryStream = entry.Open())
                                   {
                                       // Create a memory-mapped file
                                       using (MemoryMappedFile mmf =
                                              MemoryMappedFile.CreateFromFile(
                                                  Path.Combine(this._largeFileCacheDir, tempFileName),
                                                  FileMode.OpenOrCreate,
                                                  null,
                                                  entry.Length))
                                       {
                                           // Create a view stream for the memory-mapped file
                                           using (MemoryMappedViewStream viewStream = mmf.CreateViewStream())
                                           {
                                               // Copy the source stream to the view stream
                                               entryStream.CopyTo(viewStream);
                                           }
                                       }
                                   }

                                   MemoryMappedFile mmf2 = MemoryMappedFile.CreateFromFile(
                                       Path.Combine(this._largeFileCacheDir, tempFileName),
                                       FileMode.OpenOrCreate,
                                       null,
                                       entry.Length);

                                   MemoryMappedViewAccessor accessor = mmf2.CreateViewAccessor(0, entry.Length);
                                   this._logger.LogDebug("End Caching {LargeOrSmall} file: {FileName}", "LARGE", fileName);
                                   return new LargeFileCacheEntry(
                                       Path.Combine(this._largeFileCacheDir, tempFileName),
                                       mmf2,
                                       accessor,
                                       this._largeFileCacheEntryLogger);
                               }, fileSize
                           ))
                    {
                        if (largeFileCacheItem == null)
                        {
                            throw new FileNotFoundException();
                        }

                        if (offset >= fileSize)
                        {
                            bytesRead = 0;
                        }
                        else
                        {
                            // Read from the memory-mapped file to the buffer
                            bytesRead = largeFileCacheItem.CacheItemValue.MemoryMappedViewAccessor.ReadArray<byte>(
                                offset, buffer, 0,
                                buffer.Length);
                        }

                        return DokanResult.Success;
                    }
                }
            }
            finally
            {
                this._zipArchivePool.Return(archive);
            }
        }
        catch (FileNotFoundException)
        {
            bytesRead = 0;
            return DokanResult.InternalError;
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.NotImplemented;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        try
        {
            EntryNode<ZipEntryAttr> node = this.LocateNode(parts);
            fileInfo = node.FileInformation;
            return DokanResult.Success;
        }
        catch (FileNotFoundException)
        {
            fileInfo = new FileInformation();
            return DokanResult.FileNotFound;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        lock (this._zipFileLock)
        {
            VerboseZipArchive archive = this._zipArchivePool.Get();
            try
            {
                if (!this._buildTree)
                {
                    this.BuildTree(this._root, archive);
                    this._buildTree = true;
                }
            }
            finally
            {
                this._zipArchivePool.Return(archive);
            }
        }

        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        try
        {
            EntryNode<ZipEntryAttr> node = this.LocateNode(parts);
            if (node.IsDirectory)
            {
                files = node.ChildNodes.Values.Select(x => x.FileInformation).ToList();
                return DokanResult.Success;
            }
            else
            {
                files = Array.Empty<FileInformation>();
                return DokanResult.InvalidParameter;
            }
        }
        catch (FileNotFoundException)
        {
            files = Array.Empty<FileInformation>();
            return DokanResult.FileNotFound;
        }
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        files = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime,
        IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus GetDiskFreeSpace(
        out long freeBytesAvailable,
        out long totalBytes,
        out long totalFreeBytes,
        IDokanFileInfo info)
    {
        freeBytesAvailable = 0L;
        totalBytes = this._zipFileInfo.Length;
        totalFreeBytes = 0L;
        return DokanResult.Success;
    }


    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = Path.GetFileNameWithoutExtension(this._filePath);
        features = FileSystemFeatures.ReadOnlyVolume;
        fileSystemName = "ZipFS";
        maximumComponentLength = 65535;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections,
        IDokanFileInfo info)
    {
        security = new FileSecurity();
        return DokanResult.Success;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
        IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    private EntryNode<ZipEntryAttr> BuildTree(EntryNode<ZipEntryAttr> root, ZipArchive zipFile)
    {
        foreach (ZipArchiveEntry entry in zipFile.Entries)
        {
            EntryNode<ZipEntryAttr> currentNode = root;
            string[] parts = entry.ParsePath();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (!string.IsNullOrEmpty(part))
                {
                    if (i < parts.Length - 1)
                    {
                        // not last part
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = 0L
                            };
                            EntryNode<ZipEntryAttr> childNode =
                                new EntryNode<ZipEntryAttr>(true, part, currentNode, fileInfo);
                            currentNode.AddChild(childNode);
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                    else
                    {
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            // last part
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = (entry.IsDirectory() ? FileAttributes.Directory : FileAttributes.Normal) |
                                             FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = entry.Length
                            };
                            ZipEntryAttr zipEntryAttr = new ZipEntryAttr(entry.FullName);
                            EntryNode<ZipEntryAttr> childNode =
                                new EntryNode<ZipEntryAttr>(entry.IsDirectory(), part, currentNode, fileInfo,
                                    zipEntryAttr);
                            currentNode.AddChild(childNode);
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                }
            }
        }

        return root;
    }

    private EntryNode<ZipEntryAttr> LocateNode(string[] parts)
    {
        if (parts.Length == 0) //root
        {
            return this._root;
        }
        else
        {
            EntryNode<ZipEntryAttr> currentNode = this._root;
            foreach (string part in parts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    if (currentNode.ChildNodes.ContainsKey(part))
                    {
                        currentNode = currentNode.ChildNodes[part];
                    }
                    else
                    {
                        throw new FileNotFoundException();
                    }
                }
            }

            return currentNode;
        }
    }

    public void Dispose()
    {
        this._smallFileCache.Dispose();
        this._largeFileCache.Dispose();
    }
}