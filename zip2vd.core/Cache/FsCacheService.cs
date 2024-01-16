using System.IO.Compression;
using BitFaster.Caching.Lru;
using Microsoft.Extensions.Logging;
using zip2vd.core.Proxy.FsNode;

namespace zip2vd.core.Cache;

public class FsCacheService : IDisposable
{
    private LruMemoryCache<string, byte[]> _smallFileCache;
    private LruMemoryCache<string, LargeFileCacheEntry> _largeFileCache;
    private ClassicLru<string, IReadOnlyList<IFsTreeNode>> _treeCache;

    public FsCacheService(ILoggerFactory loggerFactory)
    {
        this._smallFileCache = new LruMemoryCache<string, byte[]>(100L*1024L*1024L, loggerFactory);
        this._largeFileCache =
            new LruMemoryCache<string, LargeFileCacheEntry>(10L*1024L*1024L*1024L, loggerFactory);
        this._treeCache = new ClassicLru<string, IReadOnlyList<IFsTreeNode>>(1000);
    }

    public LruMemoryCache<string, byte[]> SmallFileCache => this._smallFileCache;
    public LruMemoryCache<string, LargeFileCacheEntry> LargeFileCache => this._largeFileCache;
    public ClassicLru<string, IReadOnlyList<IFsTreeNode>> TreeCache => this._treeCache;

    public void Dispose()
    {
        this._smallFileCache.Dispose();
        this._largeFileCache.Dispose();
        this._treeCache.Clear();
    }
}