using System.IO.Compression;
using BitFaster.Caching.Lru;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using zip2vd.core.Proxy.FsNode;

namespace zip2vd.core.Cache;

public class FsCacheService : IDisposable
{
    private readonly LruMemoryCache<string, byte[]> _smallFileCache;
    private readonly LruMemoryCache<string, LargeFileCacheEntry> _largeFileCache;
    private readonly ClassicLru<string, IReadOnlyList<IFsTreeNode>> _treeCache;
    private readonly LruMemoryCache<string, ObjectPool<VerboseZipArchive>> _archivePoolCache;

    public FsCacheService(ILoggerFactory loggerFactory)
    {
        this._smallFileCache = new LruMemoryCache<string, byte[]>(1000L*1024L*1024L, loggerFactory);
        this._largeFileCache =
            new LruMemoryCache<string, LargeFileCacheEntry>(10L*1024L*1024L*1024L, loggerFactory);
        this._treeCache = new ClassicLru<string, IReadOnlyList<IFsTreeNode>>(30);
        this._archivePoolCache = new LruMemoryCache<string, ObjectPool<VerboseZipArchive>>(30, loggerFactory);
    }

    public LruMemoryCache<string, byte[]> SmallFileCache => this._smallFileCache;
    public LruMemoryCache<string, LargeFileCacheEntry> LargeFileCache => this._largeFileCache;
    public ClassicLru<string, IReadOnlyList<IFsTreeNode>> TreeCache => this._treeCache;
    public LruMemoryCache<string, ObjectPool<VerboseZipArchive>> ArchivePoolCache => this._archivePoolCache;

    public void Dispose()
    {
        this._smallFileCache.Dispose();
        this._largeFileCache.Dispose();
        this._treeCache.Clear();
        this._archivePoolCache.Dispose();
    }
}