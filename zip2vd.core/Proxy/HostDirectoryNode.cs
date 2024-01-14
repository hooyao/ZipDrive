using System.Collections.Concurrent;
using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.FileSystem;

namespace zip2vd.core.Proxy;

public class HostDirectoryNode : AbstractFsTreeNode<HostDirectoryNodeAttributes>
{
    private readonly ILogger<HostDirectoryNode> _logger;
    private readonly Lazy<FileInformation> _lazyFileInfo;

    private readonly ConcurrentDictionary<string, IFsTreeNode> _childNodes = new ConcurrentDictionary<string, IFsTreeNode>();

    private volatile bool _isChildNodesReady = false;

    private object _nodeLock = new object();

    public HostDirectoryNode(
        string name,
        IFsTreeNode? parent,
        HostDirectoryNodeAttributes attributes,
        ILoggerFactory loggerFactory) :
        base(name, FsTreeNodeType.HostDirectory, attributes, loggerFactory)
    {
        this._logger = loggerFactory.CreateLogger<HostDirectoryNode>();
        this._lazyFileInfo = new Lazy<FileInformation>(() =>
        {
            //DirectoryInfo di = new DirectoryInfo(attributes.HostAbsolutePath);
            return new FileInformation
            {
                Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                FileName = name,
                LastAccessTime = DateTime.UtcNow,
                LastWriteTime = DateTime.UtcNow,
                CreationTime = DateTime.UtcNow,
                Length = 0L
            };
        });

        this.Parent = parent;
    }

    public override bool IsDirectory => true;

    public override int ReadFile(byte[] buffer, long offset)
    {
        if (this.Name == "/")
        {
            return 0;
        }
        throw new NotSupportedException("Not able to read directory");
    }

    public override FileInformation FileInformation => this._lazyFileInfo.Value;
    public override IFsTreeNode? Parent { get; set; }

    public void SetChildNodsReady()
    {
        this._isChildNodesReady = true;
    }

    public void AddChildren(IReadOnlyList<IFsTreeNode> children)
    {
        lock (this._nodeLock)
        {
            foreach (IFsTreeNode child in children)
            {
                child.Parent = this;
                this._childNodes.AddOrUpdate(child.Name, child, (_, _) => child);
            }
        }
    }

    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes
    {
        get
        {
            lock (this._nodeLock)
            {
                if (!this._isChildNodesReady)
                {
                    // Build true
                    Stack<IFsTreeNode> stack = new Stack<IFsTreeNode>();
                    stack.Push(this);

                    while (stack.Count > 0)
                    {
                        IFsTreeNode currentNode = stack.Pop();
                        if (currentNode is HostDirectoryNode hdn && !string.IsNullOrEmpty(hdn.Attributes?.HostAbsolutePath))
                        {
                            List<IFsTreeNode> children = new List<IFsTreeNode>(10);
                            try
                            {
                                DirectoryInfo di = new DirectoryInfo(hdn.Attributes.HostAbsolutePath);
                                foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos("*"))
                                {
                                    if ((fsi.Attributes & FileAttributes.Directory) != 0)
                                    {
                                        string directoryName = Path.GetFileNameWithoutExtension(fsi.FullName);
                                        HostDirectoryNode node = new HostDirectoryNode(
                                            directoryName,
                                            this,
                                            new HostDirectoryNodeAttributes(fsi.FullName),
                                            this.LoggerFactory);
                                        children.Add(node);
                                        stack.Push(node);
                                    }
                                    else if (Constants.SupportedArchiveExts.Contains(fsi.Extension))
                                    {
                                        string fileName = Path.GetFileNameWithoutExtension(fsi.FullName);
                                        HostZipFileNode node = new HostZipFileNode(
                                            fileName,
                                            new HostZipFileNodeAttribute(),
                                            this.LoggerFactory);
                                        children.Add(node);
                                    }
                                }
                                hdn.AddChildren(children);
                                hdn.SetChildNodsReady();
                            }
                            catch (Exception ex)
                            {
                                this._logger.LogError(ex, "Failed to enumerate directory {DirectoryPath}", hdn.Attributes.HostAbsolutePath);
                            }
                        }
                    }
                    this.SetChildNodsReady();
                }

                return this._childNodes.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }
    }
}