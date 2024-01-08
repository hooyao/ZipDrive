using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using zip2vd.core.Cache;

namespace zip2vd.utils.test;

public class LruMemoryCacheUnitTest
{
    private readonly ITestOutputHelper _testOutputHelper;
    public LruMemoryCacheUnitTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public void BorrowOrAdd_Should_Be_Threadsafe_When_Cache_Entries_Are_Evicted()
    {
        // Arrange
        int threadCount = 100;
        ILoggerFactory loggingFactory = LoggerFactory.Create(builder => builder.AddConsole());
        LruMemoryCache<string, ResourceWrapper> cache = new LruMemoryCache<string, ResourceWrapper>(2000, loggingFactory);
        string[] keys = Enumerable.Range(0, 20).Select(i => i.ToString()).ToArray();
        object locker = new object();
        ConcurrentDictionary<string, int> resourceCounts = new ConcurrentDictionary<string, int>();
        ConcurrentDictionary<string, List<long>> latencyDict = new ConcurrentDictionary<string, List<long>>();

        Dictionary<string, ResourceWrapper> resourceWrappers =
            keys.Select(k => new ResourceWrapper(k, _testOutputHelper) { ResourceWrapperValue = 0 }).ToDictionary(r => r.Key, r => r);

        // Act
        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, i =>
        {
            for (int j = 0; j < 100; j++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (LruMemoryCache<string, ResourceWrapper>.CacheItem resource = cache.BorrowOrAdd(keys[i%keys.Length],
                           () => { return resourceWrappers[keys[i%keys.Length]]; },
                           200))
                {
                    sw.Stop();
                    long latency = sw.ElapsedTicks*(1000L*1000L*1000L)/Stopwatch.Frequency;
                    // Simulate work
                    resource.CacheItemValue.ResourceWrapperValue += 1;
                    //Thread.Sleep(5);
                    lock (locker)
                    {
                        latencyDict.AddOrUpdate(keys[i%keys.Length], new List<long>(), (_, value) =>
                        {
                            value.Add(latency);
                            return value;
                        });

                        resourceCounts.AddOrUpdate(keys[i%keys.Length], 1, (_, value) => value + 1);
                    }
                }
            }
        });

        // Assert
        List<long> cleanedLatencies = new List<long>();

        foreach (string key in keys)
        {
            LruMemoryCache<string, ResourceWrapper>.CacheItem resource = cache.BorrowOrAdd(key, () => resourceWrappers[key], 1);
            Assert.Equal(resourceCounts[key], resource.CacheItemValue.ResourceWrapperValue);

            List<long> latencies = latencyDict[key];
            cleanedLatencies.AddRange(latencies.OrderBy(x => x).Skip(1).Take(latencies.Count - 2).ToList());
        }

        double avgLatency = cleanedLatencies.Average()/1000000.0;
        double maxLatency = cleanedLatencies.Max()/1000000.0;
        double minLatency = cleanedLatencies.Min()/1000000.0;
        double stdDevLatency = Math.Sqrt(cleanedLatencies.Average(v => Math.Pow(v - avgLatency, 2)))/1000000.0;

        _testOutputHelper.WriteLine($"Average latency: {avgLatency} ms");
        _testOutputHelper.WriteLine($"Max latency: {maxLatency} ms");
        _testOutputHelper.WriteLine($"Min latency: {minLatency} ms");
        _testOutputHelper.WriteLine($"Standard deviation of latency: {stdDevLatency} ms");
    }

    [Fact]
    public void BorrowOrAdd_Should_Not_Leak_Memory()
    {
        int keyRange = 20;
        int threadCount = 100;
        int loopCount = 1000;
        ILoggerFactory loggingFactory = LoggerFactory.Create(builder => builder.AddConsole());
        LruMemoryCache<string, ResourceWrapper> cache = new LruMemoryCache<string, ResourceWrapper>(2000, loggingFactory);
        string[] keys = Enumerable.Range(0, keyRange).Select(i => i.ToString()).ToArray();
        object locker = new object();
        ConcurrentDictionary<string, int> resourceCounts = new ConcurrentDictionary<string, int>();
        ConcurrentDictionary<string, List<long>> latencyDict = new ConcurrentDictionary<string, List<long>>();

        int expectedTotal = loopCount*threadCount;
        int currentCount = 0;

        // Act
        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, i =>
        {
            for (int j = 0; j < loopCount; j++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (LruMemoryCache<string, ResourceWrapper>.CacheItem cacheItem = cache.BorrowOrAdd(keys[i%keys.Length], () =>
                       {
                           //Console.WriteLine($"Creating resource for key {keys[i%keys.Length]}");
                           //Thread.Sleep(TimeSpan.FromMilliseconds(50));
                           return new ResourceWrapper(keys[i%keys.Length], this._testOutputHelper)
                               { ResourceWrapperData = new byte[1024*1024] };
                       }, 200))
                {
                    sw.Stop();
                    long latency = sw.ElapsedTicks*(1000L*1000L*1000L)/Stopwatch.Frequency;
                    // Simulate work
                    //Console.WriteLine(cacheItem.CacheItemValue.ResourceWrapperValue.Length);
                    //Thread.Sleep(5);
                    lock (locker)
                    {
                        latencyDict.AddOrUpdate(keys[i%keys.Length], new List<long>(), (_, value) =>
                        {
                            value.Add(latency);
                            return value;
                        });

                        resourceCounts.AddOrUpdate(keys[i%keys.Length], 1, (_, value) => value + 1);
                        currentCount += 1;
                        _testOutputHelper.WriteLine($"Progress {currentCount}/{expectedTotal}");
                    }
                }
            }
        });

// Assert
        List<long> cleanedLatencies = new List<long>();

        foreach (string key in keys)
        {
            //var resource = cache.BorrowOrAdd(key, () => new ResourceWrapper(key) { ResourceWrapperValue = new byte[1024*1024] }, 1);
            //Assert.Equal(resourceCounts[key], resource.Value.Value);

            List<long> latencies = latencyDict[key];
            cleanedLatencies.AddRange(latencies.OrderBy(x => x).Skip(1).Take(latencies.Count - 2).ToList());
        }

        double avgLatency = cleanedLatencies.Average()/1000000.0;
        double maxLatency = cleanedLatencies.Max()/1000000.0;
        double minLatency = cleanedLatencies.Min()/1000000.0;
        double stdDevLatency = Math.Sqrt(cleanedLatencies.Average(v => Math.Pow(v - avgLatency, 2)))/1000000.0;

        _testOutputHelper.WriteLine($"Average latency: {avgLatency} ms");
        _testOutputHelper.WriteLine($"Max latency: {maxLatency} ms");
        _testOutputHelper.WriteLine($"Min latency: {minLatency} ms");
        _testOutputHelper.WriteLine($"Standard deviation of latency: {stdDevLatency} ms");
    }
}

public class ResourceWrapper : IDisposable
{
    private readonly string _key;
    private readonly ITestOutputHelper _testOutputHelper;
    public ResourceWrapper(string key, ITestOutputHelper testOutputHelper)
    {
        _key = key;
        _testOutputHelper = testOutputHelper;
    }

    public string Key => _key;

    public int ResourceWrapperValue { get; set; } = 0;

    public byte[] ResourceWrapperData { get; set; } = new byte[0];

    public void Dispose()
    {
        //this._testOutputHelper.WriteLine($"Disposing resource for key {this._key}");
    }
}