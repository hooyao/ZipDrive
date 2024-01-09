using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Templates;
using System.Text;
using Microsoft.Extensions.Configuration;
using zip2vd.cli;
using zip2vd.core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureAppConfiguration(config =>
{
    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.custom.json", optional: true, reloadOnChange: true);
});


//var builder = Host.CreateApplicationBuilder(args);
builder.UseSerilog((hostingContext, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(hostingContext.Configuration)
    .ReadFrom.Services(services));

builder.ConfigureServices(services =>
{
    services.Configure<FileVdOptions>(options =>
    {
        options.FilePath = "D:\\test1.zip";
        options.MountPath = "R:\\";
    });
    services.AddSingleton<IVdService, FileVdService>();
    services.AddHostedService<FsHostedService>();
});

using IHost host = builder.Build();
await host.RunAsync();