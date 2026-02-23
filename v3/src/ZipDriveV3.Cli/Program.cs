using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Domain.Models;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Caching;
using ZipDriveV3.Infrastructure.FileSystem;
using MountOptions = ZipDriveV3.Infrastructure.FileSystem.MountOptions;

// Required for ZIP entry name encoding
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Console.WriteLine("ZipDrive V3 starting...");

var builder = Host.CreateDefaultBuilder(args);

builder.UseSerilog((context, config) =>
{
    config
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console();  // Ensure console output even if config fails
});

builder.ConfigureServices((context, services) =>
{
    // Bind configuration sections
    services.Configure<MountOptions>(context.Configuration.GetSection("Mount"));
    services.Configure<CacheOptions>(context.Configuration.GetSection("Cache"));

    // Archive trie (platform-aware case sensitivity)
    IEqualityComparer<char>? charComparer = OperatingSystem.IsWindows()
        ? CaseInsensitiveCharComparer.Instance
        : null;
    services.AddSingleton<IArchiveTrie>(new ArchiveTrie(charComparer));

    // Path resolver
    services.AddSingleton<IPathResolver>(sp =>
        new PathResolver(sp.GetRequiredService<IArchiveTrie>()));

    // Archive discovery
    services.AddSingleton<IArchiveDiscovery, ArchiveDiscovery>();

    // ZIP reader factory
    services.AddSingleton<Func<string, IZipReader>>(sp =>
        path => new ZipReader(File.OpenRead(path)));

    // Cache infrastructure
    services.AddSingleton<IEvictionPolicy, LruEvictionPolicy>();

    services.AddSingleton<ICache<ArchiveStructure>>(sp =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>().Value;
        var storage = new ObjectStorageStrategy<ArchiveStructure>();
        var eviction = sp.GetRequiredService<IEvictionPolicy>();
        // Use a reasonable capacity for structure cache (256 MB)
        return new GenericCache<ArchiveStructure>(storage, eviction, 256 * 1024 * 1024);
    });

    services.AddSingleton<IArchiveStructureCache>(sp =>
        new ArchiveStructureCache(
            sp.GetRequiredService<ICache<ArchiveStructure>>(),
            sp.GetRequiredService<Func<string, IZipReader>>()));

    services.AddSingleton<ICache<Stream>>(sp =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>().Value;
        var storage = new MemoryStorageStrategy();
        var eviction = sp.GetRequiredService<IEvictionPolicy>();
        long capacityBytes = options.MemoryCacheSizeMb * 1024L * 1024L;
        return new GenericCache<Stream>(storage, eviction, capacityBytes);
    });

    // VFS
    services.AddSingleton<IVirtualFileSystem>(sp =>
        new ZipVirtualFileSystem(
            sp.GetRequiredService<IArchiveTrie>(),
            sp.GetRequiredService<IArchiveStructureCache>(),
            sp.GetRequiredService<ICache<Stream>>(),
            sp.GetRequiredService<IArchiveDiscovery>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<Func<string, IZipReader>>()));

    // Dokan adapter and hosted service
    services.AddSingleton<DokanFileSystemAdapter>();
    services.AddHostedService<DokanHostedService>();
});

var host = builder.Build();
await host.RunAsync();
