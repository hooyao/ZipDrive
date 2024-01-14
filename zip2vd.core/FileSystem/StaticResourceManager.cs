using System.Text;

namespace zip2vd.core.FileSystem;

public static class StaticResourceManager
{
    private static readonly Lazy<byte[]> _zipDesktopIni = new Lazy<byte[]>(() =>
    {
        using (MemoryStream tms = new MemoryStream())
        {
            tms.Write(new byte[] { 0xFF, 0xFE });
            tms.Write(Encoding.Unicode.GetBytes(new StringBuilder()
                .AppendLine("[.ShellClassInfo]")
                .AppendLine("IconResource=%SystemRoot%\\system32\\imageres.dll,165")
                .AppendLine("[ViewState]")
                .AppendLine("Mode=")
                .AppendLine("Vid=")
                .AppendLine("FolderType=Generic")
                .ToString()));
            return tms.ToArray();
        }
    });

    public static byte[] ZipDesktopIni => _zipDesktopIni.Value;
}