// See https://aka.ms/new-console-template for more information

using System.Collections.Concurrent;
using System.Diagnostics;
using zip2vd.core.Cache;

int keyRange = 9;
int threadCount = 10;
int loopCount = 10000;
var cache = new LruMemoryCache<string, ResourceWrapper>(2000);
var keys = Enumerable.Range(0, keyRange).Select(i => i.ToString()).ToArray();
var locker = new object();
var resourceCounts = new ConcurrentDictionary<string, int>();
var latencyDict = new ConcurrentDictionary<string, List<long>>();

var expectedTotal = loopCount*threadCount;
var currentCount = 0;

// Act
Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, i =>
{
    for (int j = 0; j < loopCount; j++)
    {
        var sw = Stopwatch.StartNew();
        using (var cacheItem = cache.BorrowOrAdd(keys[i%keys.Length], () =>
               {
                   //Console.WriteLine($"Creating resource for key {keys[i%keys.Length]}");
                   //Thread.Sleep(TimeSpan.FromMilliseconds(50));
                   return new ResourceWrapper(keys[i%keys.Length]) { ResourceWrapperValue = new byte[1024*1024] };
               }, 200))
        {
            sw.Stop();
            var latency = sw.ElapsedTicks*(1000L*1000L*1000L)/Stopwatch.Frequency;
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
                Console.WriteLine($"Progress {currentCount}/{expectedTotal}");
            }
        }
    }
});

// Assert
var cleanedLatencies = new List<long>();

foreach (var key in keys)
{
    //var resource = cache.BorrowOrAdd(key, () => new ResourceWrapper(key) { ResourceWrapperValue = new byte[1024*1024] }, 1);
    //Assert.Equal(resourceCounts[key], resource.Value.Value);

    var latencies = latencyDict[key];
    cleanedLatencies.AddRange(latencies.OrderBy(x => x).Skip(1).Take(latencies.Count - 2).ToList());
}

var avgLatency = cleanedLatencies.Average()/1000000.0;
var maxLatency = cleanedLatencies.Max()/1000000.0;
var minLatency = cleanedLatencies.Min()/1000000.0;
var stdDevLatency = Math.Sqrt(cleanedLatencies.Average(v => Math.Pow(v - avgLatency, 2)))/1000000.0;

Console.WriteLine($"Average latency: {avgLatency} ms");
Console.WriteLine($"Max latency: {maxLatency} ms");
Console.WriteLine($"Min latency: {minLatency} ms");
Console.WriteLine($"Standard deviation of latency: {stdDevLatency} ms");


public class ResourceWrapper : IDisposable
{
    private readonly string _key;
    public ResourceWrapper(string key)
    {
        _key = key;
    }

    public byte[] ResourceWrapperValue { get; set; }

    public void Dispose()
    {
        // Console.WriteLine($"Disposing resource for key {this._key}");
    }
}