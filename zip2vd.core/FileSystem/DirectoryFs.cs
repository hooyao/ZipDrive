using System.Security.AccessControl;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.Cache;
using zip2vd.core.Proxy;
using zip2vd.core.Proxy.FsNode;
using zip2vd.core.Proxy.NodeAttributes;
using FileAccess = DokanNet.FileAccess;

namespace zip2vd.core.FileSystem;

public class DirectoryFs : IDokanOperations, IDisposable
{
    private readonly string _directoryPath;
    private readonly HostDirectoryProxy _hostDirectoryProxy;
    private readonly FsCacheService _cacheService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DirectoryFs> _logger;

    private readonly IFsTreeNode _root;
    public DirectoryFs(string directoryPath, HostDirectoryProxy hostDirectoryProxy, FsCacheService cacheService, ILoggerFactory loggerFactory)
    {
        this._logger = loggerFactory.CreateLogger<DirectoryFs>();

        if (!Directory.Exists(directoryPath))
        {
            this._logger.LogCritical("Directory {DirectoryPath} not found", directoryPath);
            throw new DirectoryNotFoundException("Directory not found");
        }

        this._directoryPath = directoryPath;
        this._hostDirectoryProxy = hostDirectoryProxy;
        this._cacheService = cacheService;
        this._loggerFactory = loggerFactory;

        this._root = new HostDirectoryNode("/", null, new HostDirectoryNodeAttributes(directoryPath), this._cacheService, this._loggerFactory);
    }

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        IFsTreeNode node = this.LocateNode(parts);
        bytesRead = node.ReadFile(buffer, offset);
        return DokanResult.Success;
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.NotImplemented;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        this._logger.LogDebug("GetFileInformation:{FileName}", fileName);
        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var node = this.LocateNode(parts);
            fileInfo = node.FileInformation;
            return DokanResult.Success;
        }
        catch (FileNotFoundException)
        {
            fileInfo = new FileInformation();
            return DokanResult.FileNotFound;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        this._logger.LogDebug("FindFiles:{FileName}", fileName);
        string[] parts = fileName.Split(new char[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        try
        {
            IFsTreeNode node = this.LocateNode(parts);
            if (node.IsDirectory)
            {
                files = node.ChildNodes.Values.Select(x => x.FileInformation).ToList();
                return DokanResult.Success;
            }
            else
            {
                files = Array.Empty<FileInformation>();
                return DokanResult.InvalidParameter;
            }
        }
        catch (FileNotFoundException)
        {
            files = Array.Empty<FileInformation>();
            return DokanResult.FileNotFound;
        }
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        files = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime,
        IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus GetDiskFreeSpace(
        out long freeBytesAvailable,
        out long totalBytes,
        out long totalFreeBytes,
        IDokanFileInfo info)
    {
        freeBytesAvailable = 0L;
        totalBytes = 0L;
        totalFreeBytes = 0L;
        return DokanResult.Success;
    }


    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = Path.GetFileNameWithoutExtension(Path.GetFileName(this._directoryPath));
        features = FileSystemFeatures.ReadOnlyVolume;
        fileSystemName = "ZipFolderFS";
        maximumComponentLength = 65535;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections,
        IDokanFileInfo info)
    {
        security = new FileSecurity();
        return DokanResult.Success;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
        IDokanFileInfo info)
    {
        return DokanResult.NotImplemented;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    private IFsTreeNode LocateNode(string[] parts)
    {
        if (parts.Length == 0) //root
        {
            return this._root;
        }
        else
        {
            IFsTreeNode currentNode = this._root;
            foreach (string part in parts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    if (currentNode.ChildNodes.ContainsKey(part))
                    {
                        currentNode = currentNode.ChildNodes[part];
                    }
                    else
                    {
                        throw new FileNotFoundException();
                    }
                }
            }

            return currentNode;
        }
    }
}