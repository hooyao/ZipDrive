using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

    // OpenTelemetry
    var otlpEndpoint = context.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317";

    services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("ZipDriveV3"))
        .WithMetrics(m => m
            .AddMeter("ZipDriveV3.Caching")
            .AddMeter("ZipDriveV3.Zip")
            .AddMeter("ZipDriveV3.Dokan")
            .AddRuntimeInstrumentation()
            .AddProcessInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
        .WithTracing(t => t
            .AddSource("ZipDriveV3.Caching")
            .AddSource("ZipDriveV3.Zip")
            .AddSource("ZipDriveV3.Dokan")
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

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
        return new GenericCache<ArchiveStructure>(storage, eviction, 256 * 1024 * 1024, name: "structure");
    });

    services.AddSingleton<IArchiveStructureCache>(sp =>
        new ArchiveStructureCache(
            sp.GetRequiredService<ICache<ArchiveStructure>>(),
            sp.GetRequiredService<Func<string, IZipReader>>()));

    // Dual-tier file content cache (memory + disk)
    services.AddSingleton<DualTierFileCache>(sp =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>().Value;
        var eviction = sp.GetRequiredService<IEvictionPolicy>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        return new DualTierFileCache(options, eviction,
            logger: loggerFactory.CreateLogger<DualTierFileCache>(),
            loggerFactory: loggerFactory);
    });

    services.AddSingleton<ICache<Stream>>(sp => sp.GetRequiredService<DualTierFileCache>());

    // VFS
    services.AddSingleton<IVirtualFileSystem>(sp =>
        new ZipVirtualFileSystem(
            sp.GetRequiredService<IArchiveTrie>(),
            sp.GetRequiredService<IArchiveStructureCache>(),
            sp.GetRequiredService<ICache<Stream>>(),
            sp.GetRequiredService<IArchiveDiscovery>(),
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<Func<string, IZipReader>>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ZipVirtualFileSystem>()));

    // Dokan adapter and hosted service
    services.AddSingleton<DokanFileSystemAdapter>();
    services.AddHostedService<DokanHostedService>();
});

var host = builder.Build();
await host.RunAsync();
