using DokanNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using zip2vd.core.Cache;
using zip2vd.core.Common;
using zip2vd.core.Configuration;
using zip2vd.core.FileSystem;
using zip2vd.core.Proxy;

namespace zip2vd.core;

public class FileVdService : IVdService, IAsyncDisposable
{
    private readonly ILogger<FileVdService> _logger;
    private readonly string _mountPath;
    private readonly DokanInstance _dokanInstance;
    private readonly Dokan _dokan;
    private readonly ZipFs _zipFs;
    private readonly DirectoryFs _directoryFs;

    public FileVdService(
        IOptions<FileVdOptions> fileVdOptions,
        IOptions<ArchiveFileSystemOptions> archiveFileSystemOptions,
        FsCacheService cacheService,
        ILoggerFactory loggerFactory,
        ILogger<FileVdService> logger)
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
                options.Options = DokanOptions.EnableNotificationAPI;
                options.MountPoint = this._mountPath;
            });
        //this._zipFs = new ZipFs(fileVdOptions.Value.FilePath, archiveFileSystemOptions.Value, loggerFactory);

        HostDirectoryProxy hostDirectoryProxy = new HostDirectoryProxy(loggerFactory.CreateLogger<HostDirectoryProxy>());
        this._directoryFs = new DirectoryFs(fileVdOptions.Value.FolderPath, hostDirectoryProxy, cacheService, loggerFactory);
        this._dokanInstance = dokanBuilder.Build(this._directoryFs);
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