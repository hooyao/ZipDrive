using DokanNet;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy;

public class FsTreeNode
{
    private readonly Dictionary<string, FsTreeNode> _childNodes = new Dictionary<string, FsTreeNode>(10);
    private volatile bool IsChildNodeReady;

    private object _nodeLock = new object();

    public FsTreeNode(
        FsTreeNodeType nodeType,
        string name,
        FileInformation fileInformation,
        AbstractNodeAttributes? attributes)
    {
        FileInformation = fileInformation;
        Attributes = attributes;
        IsDirectory = nodeType is FsTreeNodeType.HostDirectory or FsTreeNodeType.HostZipFile or FsTreeNodeType.ZipFileDirectory;
        Name = name;
        NodeType = nodeType;
        this.IsChildNodeReady = false;
    }


    public bool IsDirectory { get; init; }

    public string Name { get; init; }
    
    public FsTreeNodeType NodeType { get; init; }

    public IReadOnlyDictionary<string, FsTreeNode> ChildNodes => this._childNodes.AsReadOnly();

    public FsTreeNode? Parent { get; set; } = null;

    public FileInformation FileInformation { get; init; }

    public AbstractNodeAttributes? Attributes { get; init; }

    public void AddChildren(IReadOnlyList<FsTreeNode> children)
    {
        if (this.IsDirectory)
        {
            lock (this._nodeLock)
            {
                if (this.IsChildNodeReady)
                {
                    return;
                }

                foreach (FsTreeNode child in children)
                {
                    child.Parent = this;
                    this._childNodes.Add(child.Name, child);
                }

                this.IsChildNodeReady = true;
            }
        }
    }
}