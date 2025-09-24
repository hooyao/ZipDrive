namespace ZipDriveV2.Caching;

public interface IFileSystemCache
{
    ValueTask<CachedItem<T>> GetOrAddAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory);
    void Remove(string key);
    void Clear();
}

public readonly struct CachedItem<T>
{
    public T Value { get; }
    public DateTimeOffset CreatedUtc { get; }
    public CachedItem(T value, DateTimeOffset createdUtc)
    {
        Value = value;
        CreatedUtc = createdUtc;
    }
}
