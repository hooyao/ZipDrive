using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using ZipDrive.Application.Services;
using ZipDrive.Domain;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Infrastructure.Archives.Rar;
using ZipDrive.Infrastructure.Archives.Zip;
using ZipDrive.Infrastructure.Caching;
using ZipDrive.Cli;
using ZipDrive.Infrastructure.FileSystem;

[assembly: SupportedOSPlatform("windows")]

// Required for ZIP entry name encoding (Shift-JIS, GBK, etc.). Code-page data is
// independent of globalization/ICU, so this works under Native AOT.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var informationalVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

// Strip the +commit-hash metadata suffix for cleaner display (e.g. "1.0.0-dev+abc123" → "1.0.0-dev")
var plusIndex = informationalVersion.IndexOf('+');
var version = plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;

// Bootstrap logger (console, Information) so the startup banner and any early
// drag-and-drop / config errors have somewhere to go before the host is built.
Log.Logger = ProgramLogging.Build(LogEventLevel.Information, LogEventLevel.Information);

Log.Information("ZipDrive {Version} starting", version);

// Drag-and-drop support: when a folder is dragged onto ZipDrive.exe,
// Windows passes the path as a bare positional arg. Rewrite it to
// --Mount:ArchiveDirectory=<path> so the config pipeline picks it up.
args = ArgPreprocessor.RewriteBareArgs(args);

var builder = Host.CreateDefaultBuilder(args)
    // Drag-and-drop sets CWD to the dragged folder, not the exe directory.
    // Force content root to the exe's directory so config files are found.
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureAppConfiguration((_, config) =>
    {
        // Note: CreateDefaultBuilder auto-adds appsettings.json (optional: true).
        // Our JSONC file layers on top and overrides any values from a .json file.
        // We ship only appsettings.jsonc; no appsettings.json exists in output.
        config.AddJsonFile("appsettings.jsonc", optional: false, reloadOnChange: false);
        config.AddJsonFile("appsettings.dev.jsonc", optional: true, reloadOnChange: false);
        // Re-add command line so it wins over jsonc files (last source wins).
        // Without this, appsettings.jsonc overrides the rewritten drag-and-drop args.
        config.AddCommandLine(args);
    })
    .ConfigureLogging((context, logging) =>
    {
        // Configure Serilog in code (Native-AOT clean — no Settings.Configuration
        // reflection, no Expressions dynamic codegen) and wire it into the
        // Microsoft.Extensions.Logging pipeline via Serilog.Extensions.Logging.
        Log.Logger = ProgramLogging.BuildFromConfiguration(context.Configuration);
        logging.ClearProviders();
        logging.AddSerilog(Log.Logger, dispose: true);
    });

builder.ConfigureServices((context, services) =>
{
    // Bind configuration sections (source-generated binding under EnableConfigurationBindingGenerator).
    services.Configure<MountSettings>(context.Configuration.GetSection("Mount"));
    services.Configure<CacheOptions>(context.Configuration.GetSection("Cache"));
    services.Configure<PrefetchOptions>(context.Configuration.GetSection("Cache:Prefetch"));

    // OpenTelemetry (opt-in: only when Endpoint is configured). The OTLP exporter and
    // SDK are Native-AOT ready; instrumentation is added only on the opt-in path.
    var otlpEndpoint = context.Configuration["OpenTelemetry:Endpoint"];

    if (!string.IsNullOrEmpty(otlpEndpoint))
    {
        // Read the interval with int.TryParse (indexer) rather than the reflection-based
        // GetValue<int> so the path stays AOT-clean.
        int metricExportIntervalSeconds =
            int.TryParse(context.Configuration["OpenTelemetry:MetricExportIntervalSeconds"], out var s) ? s : 5;
        var metricExportIntervalMs = metricExportIntervalSeconds > 0 && metricExportIntervalSeconds <= int.MaxValue / 1000
            ? metricExportIntervalSeconds * 1000
            : 5_000;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("ZipDrive"))
            .WithMetrics(m => m
                .AddMeter("ZipDrive.Caching")
                .AddMeter("ZipDrive.Zip")
                .AddMeter("ZipDrive.WinFsp")
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddOtlpExporter((exporterOptions, readerOptions) =>
                {
                    exporterOptions.Endpoint = new Uri(otlpEndpoint);
                    readerOptions.PeriodicExportingMetricReaderOptions = new PeriodicExportingMetricReaderOptions
                    {
                        ExportIntervalMilliseconds = metricExportIntervalMs
                    };
                }))
            .WithTracing(t => t
                .AddSource("ZipDrive.Caching")
                .AddSource("ZipDrive.Zip")
                .AddSource("ZipDrive.WinFsp")
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

    // Encoding detection (detector self-configures from MountSettings)
    services.AddSingleton<IFilenameEncodingDetector, FilenameEncodingDetector>();

    // Application services
    services.AddSingleton<IPathResolver, PathResolver>();
    services.AddSingleton<IArchiveDiscovery, ArchiveDiscovery>();
    services.AddSingleton<IZipReaderFactory, ZipReaderFactory>();

    // Format providers (ZIP)
    services.AddSingleton<ZipFormatMetadataStore>();
    services.AddSingleton<IArchiveStructureBuilder, ZipStructureBuilder>();
    services.AddSingleton<IArchiveEntryExtractor, ZipEntryExtractor>();
    services.AddSingleton<IPrefetchStrategy, ZipPrefetchStrategy>();

    // Format providers (RAR)
    services.AddSingleton<IArchiveStructureBuilder, RarStructureBuilder>();
    services.AddSingleton<IArchiveEntryExtractor, RarEntryExtractor>();

    services.AddSingleton<IFormatRegistry, FormatRegistry>();

    // Cache infrastructure
    services.AddSingleton<IEvictionPolicy, LruEvictionPolicy>();
    services.AddSingleton<IArchiveStructureStore, ArchiveStructureStore>();
    services.AddSingleton<IArchiveStructureCache, ArchiveStructureCache>();
    services.AddSingleton<IFileContentCache, FileContentCache>();

    // Cache maintenance (periodic eviction + cleanup)
    services.AddHostedService<CacheMaintenanceService>();

    // VFS and WinFsp
    services.AddSingleton<ArchiveVirtualFileSystem>();
    services.AddSingleton<IVirtualFileSystem>(sp => sp.GetRequiredService<ArchiveVirtualFileSystem>());
    services.AddSingleton<IArchiveManager>(sp => sp.GetRequiredService<ArchiveVirtualFileSystem>());
    services.AddSingleton<WinFspFileSystemAdapter>();
    services.AddHostedService<WinFspHostedService>();
});

var host = builder.Build();

// Standard Generic Host lifecycle. On Ctrl+C, ConsoleLifetime cancels ApplicationStopping
// and RunAsync runs each hosted service's StopAsync — WinFspHostedService unmounts the drive
// and cleans up disk-cache temp files there.
await host.RunAsync();

/// <summary>
/// Native-AOT-safe Serilog setup, configured entirely in code.
/// </summary>
internal static class ProgramLogging
{
    private const string OutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}][{SourceContext}] {Message:lj}{NewLine}{Exception}";

    public static Serilog.ILogger Build(LogEventLevel defaultLevel, LogEventLevel microsoftLevel) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(defaultLevel)
            .MinimumLevel.Override("Microsoft", microsoftLevel)
            .WriteTo.Console(outputTemplate: OutputTemplate, theme: AnsiConsoleTheme.Code)
            .CreateLogger();

    /// <summary>
    /// Builds the logger from the "Serilog:MinimumLevel" config section using plain
    /// indexer reads (no reflection binding), so it stays AOT-clean.
    /// </summary>
    public static Serilog.ILogger BuildFromConfiguration(IConfiguration configuration)
    {
        LogEventLevel defaultLevel = ParseLevel(
            configuration["Serilog:MinimumLevel:Default"], LogEventLevel.Information);
        LogEventLevel microsoftLevel = ParseLevel(
            configuration["Serilog:MinimumLevel:Override:Microsoft"], LogEventLevel.Warning);
        return Build(defaultLevel, microsoftLevel);
    }

    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse(value, ignoreCase: true, out LogEventLevel level) ? level : fallback;
}
