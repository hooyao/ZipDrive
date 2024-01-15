using System.IO.Compression;
using System.Text;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.Cache;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy.FsNode;

public class HostZipFileNode : AbstractFsTreeNode<HostZipFileNodeAttribute>
{
    private readonly FsCacheService _cacheService;
    private readonly ILogger<HostZipFileNode> _logger;

    public HostZipFileNode(
        string name,
        HostZipFileNodeAttribute attributes,
        FsCacheService cacheService,
        ILoggerFactory loggerFactory)
        : base(name, FsTreeNodeType.HostZipFile, attributes, loggerFactory)
    {
        this._cacheService = cacheService;
        this._logger = loggerFactory.CreateLogger<HostZipFileNode>();
    }
    public override bool IsDirectory => true;

    public override int ReadFile(byte[] buffer, long offset)
    {
        this._logger.LogError("Reading host zip file node {AbsolutePath} is not supported", this.Attributes?.AbsolutePath);
        throw new NotSupportedException("Host Zip file is a virtual directory, no read file operation is supported");
    }

    public override FileInformation FileInformation => new FileInformation()
    {
        Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
        FileName = this.Name,
        LastAccessTime = DateTime.Now,
        LastWriteTime = DateTime.Now,
        CreationTime = DateTime.Now,
        Length = 0L
    };

    public override IFsTreeNode? Parent { get; set; }

    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes
    {
        get
        {
            Dictionary<string, IFsTreeNode> children = new Dictionary<string, IFsTreeNode>(10);
            DesktopIniNode desktopIniNode = new DesktopIniNode("desktop.ini", this, new DesktopIniNodeAttributes(), this.LoggerFactory);
            children.Add("desktop.ini", desktopIniNode);
            Encoding ansiEncoding = Encoding.GetEncoding(0);
            using (ZipArchive zipArchive = ZipFile.Open(this.Attributes.AbsolutePath, ZipArchiveMode.Read, ansiEncoding))
            {
                int entriesCount = zipArchive.Entries.Count;
                using (var cacheItem = this._cacheService.TreeCache.BorrowOrAdd(this.Attributes.AbsolutePath, () => { return this.BuildTree(zipArchive); }, entriesCount))
                {
                    foreach (IFsTreeNode child in cacheItem.CacheItemValue)
                    {
                        child.Parent = this;
                        children.Add(child.Name, child);
                    }
                }
            }
            return children.AsReadOnly();
        }
    }

    public override void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        this._logger.LogError("Explicitly adding children to host zip file node is not supported");
        throw new NotSupportedException("Explicitly adding children to host zip file node is not supported");
    }

    private IReadOnlyList<IFsTreeNode> BuildTree(ZipArchive zipFile)
    {
        ZipFileDirectoryNode dummyRoot = new ZipFileDirectoryNode("/", new ZipFileDirectoryNodeAttributes(), this.LoggerFactory);
        foreach (ZipArchiveEntry entry in zipFile.Entries)
        {
            IFsTreeNode currentNode = dummyRoot;
            string[] parts = entry.ParsePath();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (!string.IsNullOrEmpty(part))
                {
                    if (i < parts.Length - 1)
                    {
                        // not last part
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = 0L
                            };
                            IFsTreeNode childNode =
                                new ZipFileDirectoryNode(part, new ZipFileDirectoryNodeAttributes(), this.LoggerFactory);
                            childNode.Parent = currentNode;
                            currentNode.AddChildren(new[] { childNode });
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                    else
                    {
                        if (!currentNode.ChildNodes.ContainsKey(part))
                        {
                            // last part
                            FileInformation fileInfo = new FileInformation()
                            {
                                Attributes = (entry.IsDirectory() ? FileAttributes.Directory : FileAttributes.Normal) |
                                             FileAttributes.ReadOnly,
                                CreationTime = entry.LastWriteTime.UtcDateTime,
                                FileName = part,
                                LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                Length = entry.Length
                            };
                            IFsTreeNode childNode = entry.Length == 0
                                ? new ZipFileDirectoryNode(part, new ZipFileDirectoryNodeAttributes(), this.LoggerFactory)
                                : new ZipFileItemNode(part, new ZipFileItemNodeAttributes(), this.LoggerFactory);
                            childNode.Parent = currentNode;
                            currentNode.AddChildren(new[] { childNode });
                            currentNode = childNode;
                        }
                        else
                        {
                            currentNode = currentNode.ChildNodes[part];
                        }
                    }
                }
            }
        }

        return dummyRoot.ChildNodes.Values.ToList();
    }
}