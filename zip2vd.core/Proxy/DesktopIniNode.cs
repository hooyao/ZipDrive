using DokanNet;
using Microsoft.Extensions.Logging;
using zip2vd.core.FileSystem;

namespace zip2vd.core.Proxy;

public class DesktopIniNode : AbstractFsTreeNode<DesktopIniNodeAttributes>
{
    public DesktopIniNode(string name, IFsTreeNode parent, DesktopIniNodeAttributes? attributes, ILoggerFactory loggerFactory)
        : base(name, FsTreeNodeType.DesktopIni, attributes, loggerFactory)
    {
        this.Parent = parent;
    }

    public override bool IsDirectory => false;

    public override int ReadFile(byte[] buffer, long offset)
    {
        int bytesToCopy = Math.Min(buffer.Length, StaticResourceManager.ZipDesktopIni.Length - (int)offset);
        // Copy the bytes
        StaticResourceManager.ZipDesktopIni.AsSpan().Slice((int)offset, bytesToCopy).CopyTo(buffer);
        return bytesToCopy;
    }

    public override FileInformation FileInformation => new FileInformation()
    {
        Attributes = FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.System,
        FileName = "desktop.ini",
        LastAccessTime = DateTime.Now,
        LastWriteTime = DateTime.Now,
        CreationTime = DateTime.Now,
        Length = StaticResourceManager.ZipDesktopIni.Length
    };

    public override IFsTreeNode? Parent { get; set; }
    public override IReadOnlyDictionary<string, IFsTreeNode> ChildNodes => throw new NotSupportedException("Can't get child nodes from a desktop.ini file");
}