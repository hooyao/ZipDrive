using DokanNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using zip2vd.core.Common;

namespace zip2vd.core;

public class FileVdService : IVdService, IAsyncDisposable
{
    private readonly ILogger<FileVdService> _logger;
    private readonly string _mountPath;
    private readonly DokanInstance _dokanInstance;
    private readonly Dokan _dokan;
    private readonly ZipFs _zipFs;

    public FileVdService(IOptions<FileVdOptions> fileVdOptions, ILoggerFactory loggerFactory, ILogger<FileVdService> logger)
    {
        _logger = logger;
        _mountPath = fileVdOptions.Value.MountPath;
        DokanLogger dokanLogger = new DokanLogger(loggerFactory.CreateLogger("Dokan"));
        //this._dokanLogger = new ConsoleLogger("[Dokan]");
        this._dokan = new Dokan(dokanLogger);
        DokanInstanceBuilder dokanBuilder = new DokanInstanceBuilder(this._dokan)
            .ConfigureLogger(() => dokanLogger)
            .ConfigureOptions(options =>
            {
                options.Options =  DokanOptions.EnableNotificationAPI;
                options.MountPoint = this._mountPath;
            }); 
        this._zipFs = new ZipFs(fileVdOptions.Value.FilePath, loggerFactory);
        this._dokanInstance = dokanBuilder.Build(this._zipFs);
    }

    public void Mount()
    {
        throw new NotImplementedException();
    }

    public void Unmount()
    {
        throw new NotImplementedException();
    }

    public async ValueTask DisposeAsync()
    {
        this._logger.LogInformation("DisposeAsync");
        this._dokan.RemoveMountPoint(this._mountPath);
        await this._dokanInstance.WaitForFileSystemClosedAsync(360*1000);
        
        this._dokanInstance.Dispose();
        this._dokan.Dispose();
        this._zipFs.Dispose();
    }
}