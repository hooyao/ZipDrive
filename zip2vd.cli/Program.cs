using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using zip2vd.cli;
using zip2vd.core;
using zip2vd.core.Configuration;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureAppConfiguration(config =>
{
    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.custom.json", optional: true, reloadOnChange: true)
        .AddCommandLine(args);
});

//var builder = Host.CreateApplicationBuilder(args);
builder.UseSerilog((hostingContext, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(hostingContext.Configuration)
    .ReadFrom.Services(services));

builder.ConfigureServices((hostContext, services) =>
{
    services.AddOptions<FileVdOptions>().Bind(hostContext.Configuration);
    services.AddOptions<ArchiveFileSystemOptions>().Bind(hostContext.Configuration.GetSection("zip"));
    services.AddSingleton<IVdService, FileVdService>();
    services.AddHostedService<FsHostedService>();
});


using IHost host = builder.Build();
await host.RunAsync();