using System;
using System.Threading.Tasks;
using DokanNet;
using DokanNet.Logging;
using Microsoft.Extensions.Options;

namespace zip2vd.core;

public class FileVdService : IVdService, IAsyncDisposable
{
    private readonly string _mountPath;
    private readonly ILogger _logger;
    private readonly ILogger _dokanLogger;
    private readonly DokanInstance _dokanInstance;
    private readonly Dokan _dokan;

    public FileVdService(IOptions<FileVdOptions> fileVdOptions)
    {
        _mountPath = fileVdOptions.Value.MountPath;
        this._logger = new ConsoleLogger("[Mirror]");
        this._dokanLogger = new NullLogger();
        //this._dokanLogger = new ConsoleLogger("[Dokan]");
        this._logger.Info("Constructor");
        this._dokan = new Dokan(this._dokanLogger);
        DokanInstanceBuilder dokanBuilder = new DokanInstanceBuilder(this._dokan)
            .ConfigureLogger(() => this._dokanLogger)
            .ConfigureOptions(options =>
            {
                options.Options =  DokanOptions.EnableNotificationAPI;
                options.MountPoint = this._mountPath;
            });
        ZipFs zipFs = new ZipFs(fileVdOptions.Value.FilePath);
        this._dokanInstance = dokanBuilder.Build(zipFs);
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
        this._logger.Info("DisposeAsync");
        this._dokan.RemoveMountPoint(this._mountPath);
        await this._dokanInstance.WaitForFileSystemClosedAsync(360*1000);
        
        this._dokanInstance.Dispose();
        this._dokan.Dispose();
    }
}