using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.FileSystem;
using zip2vd.core.Proxy.NodeAttributes;

namespace zip2vd.core.Proxy;

public class HostDirectoryProxy
{
    private readonly ILogger<HostDirectoryProxy> _logger;
    public HostDirectoryProxy(ILogger<HostDirectoryProxy> logger)
    {
        this._logger = logger;
    }

    public FsTreeNode BuildNodeTree(FsTreeNode root)
    {
        Stack<FsTreeNode> stack = new Stack<FsTreeNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            FsTreeNode currentNode = stack.Pop();
            if (currentNode is { NodeType: FsTreeNodeType.HostDirectory, Attributes: HostDirectoryNodeAttributes hdna })
            {
                List<FsTreeNode> children = new List<FsTreeNode>(10);
                try
                {
                    DirectoryInfo di = new DirectoryInfo(hdna.HostAbsolutePath);
                    foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos("*"))
                    {
                        if ((fsi.Attributes & FileAttributes.Directory) != 0)
                        {
                            string directoryName = Path.GetFileNameWithoutExtension(fsi.FullName);
                            FsTreeNode node = new FsTreeNode(
                                FsTreeNodeType.HostDirectory,
                                directoryName,
                                new FileInformation
                                {
                                    Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                                    FileName = directoryName,
                                    LastAccessTime = DateTime.Now,
                                    LastWriteTime = DateTime.Now,
                                    CreationTime = DateTime.Now,
                                    Length = 0L
                                }, new HostDirectoryNodeAttributes(fsi.FullName));
                            children.Add(node);
                            stack.Push(node);
                        }
                        else if (Constants.SupportedArchiveExts.Contains(fsi.Extension))
                        {
                            string fileName = Path.GetFileNameWithoutExtension(fsi.FullName);
                            FsTreeNode node = new FsTreeNode(
                                FsTreeNodeType.HostZipFile,
                                fileName,
                                new FileInformation
                                {
                                    Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
                                    FileName = fileName,
                                    LastAccessTime = DateTime.Now,
                                    LastWriteTime = DateTime.Now,
                                    CreationTime = DateTime.Now,
                                    Length = 0L
                                }, null);

                            node.AddChildren(new[]
                            {
                                new FsTreeNode(FsTreeNodeType.DesktopIni, "desktop.ini",
                                    new FileInformation
                                    {
                                        Attributes = FileAttributes.Normal | FileAttributes.ReadOnly,
                                        FileName = "desktop.ini",
                                        LastAccessTime = DateTime.Now,
                                        LastWriteTime = DateTime.Now,
                                        CreationTime = DateTime.Now,
                                        Length = StaticResourceManager.ZipDesktopIni.Length
                                    }, null)
                            });
                            children.Add(node);
                        }
                    }
                    currentNode.AddChildren(children);
                }
                catch (Exception ex)
                {
                    this._logger.LogError(ex, "Failed to enumerate directory {DirectoryPath}", hdna.HostAbsolutePath);
                }
            }
        }

        return root;
    }
}