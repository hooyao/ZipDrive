using System.Collections.Concurrent;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy.FsNode;

public class ZipFileDirectoryNode : AbstractFsTreeNode<ZipFileDirectoryNodeAttributes>
{
    private readonly ConcurrentDictionary<string, IFsTreeNode> _childNodes = new ConcurrentDictionary<string, IFsTreeNode>();

    private readonly ILogger<ZipFileDirectoryNode> _logger;

    public ZipFileDirectoryNode(
        string name,
        ZipFileDirectoryNodeAttributes? attributes,
        ILoggerFactory loggerFactory)
        : base(name, FsTreeNodeType.ZipFileDirectory, attributes, loggerFactory)
    {
        this._logger = loggerFactory.CreateLogger<ZipFileDirectoryNode>();
    }
    public override bool IsDirectory => true;

    public override int ReadFile(byte[] buffer, long offset)
    {
        this._logger.LogError("Reading zip file directory node is not supported");
        throw new NotSupportedException("Zip file directory  is a virtual directory, no read file operation is supported");
    }

    public override FileInformation FileInformation => new FileInformation()
    {
        Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
        CreationTime = DateTime.UtcNow,
        FileName = this.Name,
        LastAccessTime = DateTime.UtcNow,
        LastWriteTime = DateTime.UtcNow,
        Length = 0L
    };

    public override IFsTreeNode? Parent { get; set; }

    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes => this._childNodes.ToDictionary(pair => pair.Key, pair => pair.Value);

    public override void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        foreach (IFsTreeNode child in children)
        {
            child.Parent = this;
            this._childNodes.AddOrUpdate(child.Name, child, (_, _) => child);
        }
    }
}