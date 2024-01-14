using DokanNet;
using Microsoft.Extensions.Logging;

namespace zip2vd.core.Proxy;

public abstract class AbstractFsTreeNode<TAttr> : IFsTreeNode where TAttr : AbstractNodeAttributes
{
    protected readonly ILoggerFactory LoggerFactory;
    protected AbstractFsTreeNode(string name, FsTreeNodeType nodeType, TAttr? attributes, ILoggerFactory loggerFactory)
    {
        this.LoggerFactory = loggerFactory;
        Name = name;
        NodeType = nodeType;
        Attributes = attributes;
    }
    public TAttr? Attributes { get; }
    public string Name { get; }
    public FsTreeNodeType NodeType { get; }
    
    public abstract bool IsDirectory { get; }

    public abstract int ReadFile(byte[] buffer, long offset);
    public abstract FileInformation FileInformation { get; }
    public abstract IFsTreeNode? Parent { get; set; }
    public abstract IReadOnlyDictionary<string, IFsTreeNode> ChildNodes { get; }
}