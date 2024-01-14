using DokanNet;

namespace zip2vd.core.Proxy;

public class EntryNode<TAttr>
{
    public EntryNode(bool isDirectory, string name, EntryNode<TAttr>? parent, FileInformation fileInformation, TAttr? attributes=default)
    {
        FileInformation = fileInformation;
        Attributes = attributes;
        Parent = parent;
        IsDirectory = isDirectory;
        Name = name;
    }
    
    public TAttr? Attributes { get; private set; }

    public bool IsDirectory { get; private set; }
    
    public string Name { get; }

    private readonly Dictionary<string, EntryNode<TAttr>> _childNodes = new Dictionary<string, EntryNode<TAttr>>(10);

    public IReadOnlyDictionary<string, EntryNode<TAttr>> ChildNodes => this._childNodes.AsReadOnly();

    public EntryNode<TAttr>? Parent { get; private set; }

    public FileInformation FileInformation { get; private set; }

    public void AddChild(EntryNode<TAttr> child)
    {
        if (this.IsDirectory)
        {
            this._childNodes.Add(child.Name, child);
        }
    }
}