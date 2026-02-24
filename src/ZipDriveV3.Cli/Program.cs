using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Templates;
using Serilog.Templates.Themes;
using ZipDriveV3.Application.Services;
using ZipDriveV3.Domain;
using ZipDriveV3.Domain.Abstractions;
using ZipDriveV3.Infrastructure.Archives.Zip;
using ZipDriveV3.Infrastructure.Caching;
using ZipDriveV3.Infrastructure.FileSystem;
using MountOptions = ZipDriveV3.Infrastructure.FileSystem.MountOptions;

[assembly: SupportedOSPlatform("windows")]

// Required for ZIP entry name encoding
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Console.WriteLine("ZipDrive V3 starting...");

var builder = Host.CreateDefaultBuilder(args);

builder.UseSerilog((context, config) =>
{
    var logTheme = new TemplateTheme(new Dictionary<TemplateThemeStyle, string>
    {
        // Value types from Literate theme (colored arguments in log messages)
        [TemplateThemeStyle.String] = "\x1b[38;5;0045m",
        [TemplateThemeStyle.Number] = "\x1b[38;5;0200m",
        [TemplateThemeStyle.Boolean] = "\x1b[38;5;0027m",
        [TemplateThemeStyle.Scalar] = "\x1b[38;5;0085m",
        [TemplateThemeStyle.Null] = "\x1b[38;5;0027m",
        [TemplateThemeStyle.Name] = "\x1b[38;5;0007m",
        [TemplateThemeStyle.Invalid] = "\x1b[38;5;0011m",
        // Custom level colors
        [TemplateThemeStyle.LevelVerbose] = "\x1b[90m",
        [TemplateThemeStyle.LevelDebug] = "\x1b[90m",
        [TemplateThemeStyle.LevelInformation] = "\x1b[32m",
        [TemplateThemeStyle.LevelWarning] = "\x1b[33m",
        [TemplateThemeStyle.LevelError] = "\x1b[31m",
        [TemplateThemeStyle.LevelFatal] = "\x1b[1;31m",
    });

    config.ReadFrom.Configuration(context.Configuration)
          .WriteTo.Console(new ExpressionTemplate(
              "[{@t:HH:mm:ss} {@l:u3}][\x1b[90m{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1)}\x1b[0m] {@m}\n{#if @x is not null}{@x}\n{#end}",
              theme: logTheme));
});

builder.ConfigureServices((context, services) =>
{
    // Bind configuration sections
    services.Configure<MountOptions>(context.Configuration.GetSection("Mount"));
    services.Configure<CacheOptions>(context.Configuration.GetSection("Cache"));

    // OpenTelemetry (opt-in: only when Endpoint is configured)
    var otlpEndpoint = context.Configuration["OpenTelemetry:Endpoint"];

    if (!string.IsNullOrEmpty(otlpEndpoint))
    {
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
    }
    else
    {
        Log.Information("OpenTelemetry disabled (no OpenTelemetry:Endpoint configured)");
    }

    // Shared infrastructure
    services.AddSingleton(TimeProvider.System);

    // Archive trie (platform-aware case sensitivity)
    IEqualityComparer<char>? charComparer = OperatingSystem.IsWindows()
        ? CaseInsensitiveCharComparer.Instance
        : null;
    services.AddSingleton<IArchiveTrie>(new ArchiveTrie(charComparer));

    // Application services
    services.AddSingleton<IPathResolver, PathResolver>();
    services.AddSingleton<IArchiveDiscovery, ArchiveDiscovery>();
    services.AddSingleton<IZipReaderFactory, ZipReaderFactory>();

    // Cache infrastructure
    services.AddSingleton<IEvictionPolicy, LruEvictionPolicy>();
    services.AddSingleton<IArchiveStructureStore, ArchiveStructureStore>();
    services.AddSingleton<IArchiveStructureCache, ArchiveStructureCache>();
    services.AddSingleton<DualTierFileCache>();

    // Cache maintenance (periodic eviction + cleanup)
    services.AddHostedService<CacheMaintenanceService>();

    // VFS and Dokan
    services.AddSingleton<IVirtualFileSystem, ZipVirtualFileSystem>();
    services.AddSingleton<DokanFileSystemAdapter>();
    services.AddHostedService<DokanHostedService>();
});

var host = builder.Build();
await host.RunAsync();
