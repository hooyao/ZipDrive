using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using zip2vd.core;

namespace zip2vd.cli;

public class FsHostedService : IHostedService
{
    private readonly IVdService _vdService;
    private readonly ILogger<FsHostedService> _logger;

    public FsHostedService(IHostApplicationLifetime appLifetime, IVdService vdService, ILogger<FsHostedService> logger)
    {
        this._vdService = vdService;
        this._logger = logger;
        appLifetime.ApplicationStarted.Register(OnStarted);
        appLifetime.ApplicationStopping.Register(OnStopping);
        appLifetime.ApplicationStopped.Register(OnStopped);
    }

    private void OnStopped()
    {
        this._logger.LogInformation("OnStopped");
    }

    private void OnStopping()
    {
        this._logger.LogInformation("OnStopping");
    }

    private void OnStarted()
    {
        this._logger.LogInformation("OnStarted");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        this._logger.LogInformation("Starting");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this._logger.LogInformation("Stopping");
        return Task.CompletedTask;
    }
}