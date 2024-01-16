using DokanNet;

namespace zip2vd.core.Proxy.NodeAttributes;

public class ZipFileItemNodeAttributes : AbstractNodeAttributes
{
    public string ItemFullPath { get; init; }
    public string ZipFileAbsolutePath { get; init; }
    public FileInformation FileInformation { get; init; }
}