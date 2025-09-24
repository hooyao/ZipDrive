using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(b => b.ClearProviders().AddConsole())
    .ConfigureServices(s =>
    {
    })
    .Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("bootstrap");
logger.LogInformation("ZipDriveV2 CLI scaffold starting (no mount logic yet).");
logger.LogInformation("Args: {Args}", string.Join(' ', args));
logger.LogInformation("Shutting down.");
