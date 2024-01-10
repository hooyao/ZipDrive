using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Cache;

public class LruMemoryCache<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly long _sizeLimit;
    private readonly ILogger<LruMemoryCache<TKey, TValue>> _logger;
    private readonly ILogger<CacheItem> _cacheItemLogger;
    private long _currentSize;
    private readonly ConcurrentDictionary<TKey, LinkedListNode<CacheItem>> _cache;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly ConcurrentDictionary<TKey, Semaphore> _keyLocks;

    private readonly object _cacheLock = new object();

    private volatile bool _disposed;

    public LruMemoryCache(long sizeLimit, ILoggerFactory loggerFactory)
    {
        this._sizeLimit = sizeLimit;
        this._logger = loggerFactory.CreateLogger<LruMemoryCache<TKey, TValue>>();
        this._cacheItemLogger = loggerFactory.CreateLogger<CacheItem>();
        this._currentSize = 0;
        this._keyLocks = new ConcurrentDictionary<TKey, Semaphore>();
        this._lruList = new LinkedList<CacheItem>();
        this._cache = new ConcurrentDictionary<TKey, LinkedListNode<CacheItem>>();
        this._disposed = false;
    }

    public CacheItem BorrowOrAdd(TKey key, Func<TValue> valueFactory, long size)
    {
        CheckDisposed();
        this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Acquiring cache lock T1 for key {Key}",
            Thread.CurrentThread.ManagedThreadId, key);
        lock (this._cacheLock)
        {
            this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Acquired cache lock T1 for key {Key}", Thread.CurrentThread.ManagedThreadId, key);
            Semaphore keyLock = _keyLocks.GetOrAdd(key, new Semaphore(1, 1));

            keyLock.WaitOne();
            if (this._cache.TryGetValue(key, out LinkedListNode<CacheItem>? node))
            {
                _lruList.Remove(node);
                _lruList.AddLast(node);
                this._logger.LogTrace("Cache hit for key {Key}", key);
                return node.Value;
            }

            Lazy<TValue> lazyObject = new Lazy<TValue>(valueFactory);
            CacheItem cacheItem = new CacheItem(key, lazyObject, size, keyLock, _cacheItemLogger);
            while (this._currentSize + size >= this._sizeLimit)
            {
                LinkedListNode<CacheItem>? fistNode = this._lruList.First;
                if (this._keyLocks.TryGetValue(fistNode!.Value.Key, out var keyLockToRemove))
                {
                    this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Acquiring semaphore for key {Key} for removing",
                        Thread.CurrentThread.ManagedThreadId, fistNode!.Value.Key);
                    keyLockToRemove.WaitOne();
                    this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Acquired semaphore for key {Key} for removing",
                        Thread.CurrentThread.ManagedThreadId, fistNode!.Value.Key);
                    _lruList.RemoveFirst();
                    var removedKey = fistNode.Value.Key;
                    this._cache.TryRemove(removedKey, out var removedNode);
                    if (removedNode!.Value.CacheItemValue is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    this._currentSize -= removedNode.Value.Size;
                    this._logger.LogTrace(
                        "Thread {CurrentThreadManagedThreadId}: Releasing acquired semaphore for key {Key} for removing",
                        Thread.CurrentThread.ManagedThreadId, removedNode.Value.Key);
                    keyLockToRemove.Release();
                    this._keyLocks.TryRemove(removedKey, out _);
                    this._logger.LogDebug("Evict key {Key} from cache, Size: {Size}, Current size: {CurrentSize}, LRU List size: {LruListSize}, Key locks size: {KeyLocksSize}", 
                        removedNode.Value.Key, removedNode.Value.Size, this._currentSize, this._lruList.Count, this._keyLocks.Count);
                    this._logger.LogTrace(
                        "Thread {CurrentThreadManagedThreadId}: Released acquired semaphore for key {Key} for removing",
                        Thread.CurrentThread.ManagedThreadId, removedNode.Value.Key);
                }
                else
                {
                    throw new Exception("Key lock not found");
                }
            }

            var newNode = new LinkedListNode<CacheItem>(cacheItem);
            this._lruList.AddLast(newNode);
            this._cache.AddOrUpdate(key, newNode, (_, _) => newNode);
            this._currentSize += size;
            this._logger.LogTrace("Releasing cache lock T1 for key {Key}", key);
            return cacheItem;
        }
    }

    ~LruMemoryCache()
    {
        Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
    }

    protected virtual void Dispose(bool disposing)
    {
        lock (this._cacheLock)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    foreach (KeyValuePair<TKey, LinkedListNode<CacheItem>> cachePair in this._cache)
                    {
                        if (cachePair.Value.Value.CacheItemValue is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    GC.SuppressFinalize(this);
                }

                _disposed = true;
            }
        }
    }

    private void CheckDisposed()
    {
        lock (this._cacheLock)
        {
            if (_disposed)
            {
                Throw();
            }

            [DoesNotReturn]
            static void Throw() => throw new ObjectDisposedException(typeof(LruMemoryCache<TKey, TValue>).FullName);
        }
    }

    public class CacheItem : IDisposable
    {
        private readonly Semaphore _semaphore;
        private readonly ILogger<CacheItem> _logger;

        private readonly Lazy<TValue> _cachedLazyObject;

        public CacheItem(TKey key, Lazy<TValue> cachedLazyObject, long size, Semaphore semaphore, ILogger<CacheItem> logger)
        {
            this.Key = key;
            this._cachedLazyObject = cachedLazyObject;
            this.Size = size;
            this._semaphore = semaphore;
            _logger = logger;
        }
        public TKey Key { get; }

        public TValue CacheItemValue => this._cachedLazyObject.Value;


        public long Size { get; }

        public void Dispose()
        {
            this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Disposing cache item for key {Key}",
                Thread.CurrentThread.ManagedThreadId, this.Key);
            this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Releasing semaphore for key {Key}",
                Thread.CurrentThread.ManagedThreadId, this.Key);
            _semaphore.Release(); // release the semaphore
            this._logger.LogTrace("Thread {CurrentThreadManagedThreadId}: Released semaphore for key {Key}",
                Thread.CurrentThread.ManagedThreadId, this.Key);
        }
    }
}