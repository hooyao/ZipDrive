using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using zip2vd.core;

namespace zip2vd.cli;

public class CacheCleaner : IHostedService
{
    private readonly IVdService _vdService;
    private readonly ILogger<CacheCleaner> _logger;
    public CacheCleaner(IHostApplicationLifetime appLifetime, IVdService vdService, ILogger<CacheCleaner> logger)
    {
        this._vdService = vdService;
        this._logger = logger;
        // appLifetime.ApplicationStarted.Register(OnStarted);
        // appLifetime.ApplicationStopping.Register(OnStopping);
        // appLifetime.ApplicationStopped.Register(OnStopped);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            this._vdService.CompactCache();
            await Task.Delay(30000, cancellationToken);
        }
        
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}