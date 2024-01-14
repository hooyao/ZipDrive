using DokanNet;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Proxy;

public class HostZipFileNode : AbstractFsTreeNode<HostZipFileNodeAttribute>
{
    private readonly ILogger<HostZipFileNode> _logger;

    public HostZipFileNode(
        string name,
        HostZipFileNodeAttribute? attributes,
        ILoggerFactory loggerFactory)
        : base(name, FsTreeNodeType.HostZipFile, attributes, loggerFactory)
    {
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
        Attributes = FileAttributes.Directory| FileAttributes.ReadOnly,
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
            return children.AsReadOnly();
        }
    }
}