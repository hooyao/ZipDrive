using System.IO.Compression;
using System.Text;
using DokanNet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using zip2vd.core.Cache;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy.FsNode;

public class HostZipFileNode : AbstractFsTreeNode<HostZipFileNodeAttribute>
{
    private readonly FsCacheService _cacheService;
    private readonly DefaultObjectPoolProvider _objectPoolProvider;
    private readonly ILogger<HostZipFileNode> _logger;

    private readonly object _nodeLock = new object();

    public HostZipFileNode(
        string name,
        HostZipFileNodeAttribute attributes,
        FsCacheService cacheService,
        DefaultObjectPoolProvider objectPoolProvider,
        ILoggerFactory loggerFactory)
        : base(name, FsTreeNodeType.HostZipFile, attributes, loggerFactory)
    {
        this._cacheService = cacheService;
        this._objectPoolProvider = objectPoolProvider;
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
            lock (this._nodeLock)
            {
                if (this._cacheService.TreeCache.TryGet(this.Attributes.AbsolutePath, out IReadOnlyList<IFsTreeNode>? childNodeList))
                {
                    return childNodeList.ToDictionary(pair => pair.Name, pair => pair);
                }
                //Dictionary<string, IFsTreeNode> children = new Dictionary<string, IFsTreeNode>(10);

                Encoding ansiEncoding = Encoding.GetEncoding(0);
                IReadOnlyList<IFsTreeNode> children;
                VerboseZipArchive archive;
                using (LruMemoryCache<string, ObjectPool<VerboseZipArchive>>.CacheItem archivePoolItem = this._cacheService.ArchivePoolCache.BorrowOrAdd(
                           this.Attributes.AbsolutePath, () =>
                           {
                               ObjectPool<VerboseZipArchive> zipArchivePool =
                                   this._objectPoolProvider.Create<VerboseZipArchive>(new ZipArchivePooledObjectPolicy(this.Attributes.AbsolutePath,
                                       ansiEncoding,
                                       base.LoggerFactory));
                               return zipArchivePool;
                           }, 1))
                {
                    ObjectPool<VerboseZipArchive> pool = archivePoolItem.CacheItemValue;
                    archive = pool.Get();
                    try
                    {
                        children = this.BuildTree(archive);
                        //DesktopIniNode desktopIniNode = new DesktopIniNode("desktop.ini", this, new DesktopIniNodeAttributes(), this.LoggerFactory);
                        this._cacheService.TreeCache.AddOrUpdate(this.Attributes.AbsolutePath, children);
                        var dict = children.ToDictionary(pair => pair.Name, pair => pair);
                        // dict.Add("desktop.ini", desktopIniNode);
                        return dict;
                    }
                    finally
                    {
                        pool.Return(archive);
                    }
                }
            }
        }
    }

    public override void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        this._logger.LogError("Explicitly adding children to host zip file node is not supported");
        throw new NotSupportedException("Explicitly adding children to host zip file node is not supported");
    }

    private IReadOnlyList<IFsTreeNode> BuildTree(ZipArchive zipFile)
    {
        //this._logger.LogInformation("Building tree for archive {AbsolutePath}", this.Attributes.AbsolutePath);
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
                            if (part == "index.md")
                            {
                                this._logger.LogInformation("Pause here");
                            }
                            IFsTreeNode childNode = entry.IsDirectory()
                                ? new ZipFileDirectoryNode(part, new ZipFileDirectoryNodeAttributes(), this.LoggerFactory)
                                : new ZipFileItemNode(part, new ZipFileItemNodeAttributes()
                                {
                                    ItemFullPath = entry.FullName,
                                    ZipFileAbsolutePath = this.Attributes.AbsolutePath,
                                    FileInformation = new FileInformation()
                                    {
                                        Attributes = FileAttributes.Normal | FileAttributes.ReadOnly,
                                        CreationTime = entry.LastWriteTime.UtcDateTime,
                                        FileName = part,
                                        LastAccessTime = entry.LastWriteTime.UtcDateTime,
                                        LastWriteTime = entry.LastWriteTime.UtcDateTime,
                                        Length = entry.Length
                                    }
                                }, this._cacheService, this._objectPoolProvider, this.LoggerFactory);
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