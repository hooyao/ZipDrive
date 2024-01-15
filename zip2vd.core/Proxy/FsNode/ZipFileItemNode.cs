using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy.FsNode;

public class ZipFileItemNode : AbstractFsTreeNode<ZipFileItemNodeAttributes>
{
    private readonly ILogger<ZipFileItemNode> _logger;
    private readonly Lazy<FileInformation> _lazyFileInfo;

    private volatile bool _isChildNodesReady = false;

    private object _nodeLock = new object();

    public ZipFileItemNode(
        string name,
        ZipFileItemNodeAttributes? attributes,
        ILoggerFactory loggerFactory) :
        base(name, FsTreeNodeType.ZipFileItem, attributes, loggerFactory)
    {
        this._logger = loggerFactory.CreateLogger<ZipFileItemNode>();
    }
    public override bool IsDirectory => false;  

    public override int ReadFile(byte[] buffer, long offset)
    {
        return 0;
    }

    public override FileInformation FileInformation => new FileInformation();
    public override IFsTreeNode? Parent { get; set; }
    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes => ReadOnlyDictionary<string, IFsTreeNode>.Empty;

    public override void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        this._logger.LogError("Zip file item node does not support adding children");
        throw new NotSupportedException("Zip file item node does not support adding children");
    }
}