using DokanNet;

namespace zip2vd.core.Proxy.FsNode;

public interface IFsTreeNode
{
    public string Name { get; }

    public FsTreeNodeType NodeType { get; }

    public bool IsDirectory { get; }

    public int ReadFile(byte[] buffer, long offset);

    public FileInformation FileInformation { get; }

    public IFsTreeNode? Parent { get; set; }

    public IReadOnlyDictionary<string, IFsTreeNode> ChildNodes { get; }

    public void AddChildren(IReadOnlyList<IFsTreeNode> children);
}