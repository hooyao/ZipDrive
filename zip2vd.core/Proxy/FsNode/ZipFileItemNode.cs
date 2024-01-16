using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.Cache;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy.FsNode;

public class ZipFileItemNode : AbstractFsTreeNode<ZipFileItemNodeAttributes>
{
    private readonly FsCacheService _cacheService;
    private readonly ILogger<ZipFileItemNode> _logger;
    private readonly Lazy<FileInformation> _lazyFileInfo;

    private volatile bool _isChildNodesReady = false;

    private object _nodeLock = new object();
    private readonly long _smallFileSizeCutOff = 50L*1024L*1024L;

    private readonly string _largeFileCacheDir = Path.GetTempPath();
    public ZipFileItemNode(
        string name,
        ZipFileItemNodeAttributes? attributes,
        FsCacheService cacheService,
        ILoggerFactory loggerFactory) :
        base(name, FsTreeNodeType.ZipFileItem, attributes, loggerFactory)
    {
        this._cacheService = cacheService;
        this._logger = loggerFactory.CreateLogger<ZipFileItemNode>();
    }
    public override bool IsDirectory => false;

    public override int ReadFile(byte[] buffer, long offset)
    {
        int bytesRead = 0;

        Encoding ansiEncoding = Encoding.GetEncoding(0);
        using (ZipArchive zipArchive = ZipFile.Open(this.Attributes.ZipFileAbsolutePath, ZipArchiveMode.Read, ansiEncoding))
        {
            ZipArchiveEntry? entry = zipArchive.GetEntry(this.Attributes.ItemFullPath);
            if (entry == null)
            {
                throw new FileNotFoundException();
            }

            long fileSize = entry.Length;
            if (fileSize <= this._smallFileSizeCutOff)
            {
                string fileKey = $"{this.Attributes.ZipFileAbsolutePath}:{this.Attributes.ItemFullPath}";
                using (LruMemoryCache<string, byte[]>.CacheItem cacheItem = this._cacheService.SmallFileCache.BorrowOrAdd(
                           fileKey,
                           () =>
                           {
                               this._logger.LogDebug("Caching {LargeOrSmall} file: {FileName}", "small", fileKey);
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
                    return bytesRead;
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
                string fileKey = $"{this.Attributes.ZipFileAbsolutePath}:{this.Attributes.ItemFullPath}";
                using (LruMemoryCache<string, LargeFileCacheEntry>.CacheItem largeFileCacheItem =
                       this._cacheService.LargeFileCache.BorrowOrAdd(fileKey, () =>
                           {
                               this._logger.LogDebug("Start Caching {LargeOrSmall} file: {FileName}", "LARGE", fileKey);
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
                               this._logger.LogDebug("End Caching {LargeOrSmall} file: {FileName}", "LARGE", fileKey);
                               return new LargeFileCacheEntry(
                                   Path.Combine(this._largeFileCacheDir, tempFileName),
                                   mmf2,
                                   accessor,
                                   this.LoggerFactory.CreateLogger<LargeFileCacheEntry>());
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

                    return bytesRead;
                }
            }
        }
    }

    public override FileInformation FileInformation => this.Attributes.FileInformation;
    public override IFsTreeNode? Parent { get; set; }
    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes => ReadOnlyDictionary<string, IFsTreeNode>.Empty;

    public override void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        this._logger.LogError("Zip file item node does not support adding children");
        throw new NotSupportedException("Zip file item node does not support adding children");
    }
}