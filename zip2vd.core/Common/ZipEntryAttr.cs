namespace zip2vd.core.Common;

public struct ZipEntryAttr
{
    public ZipEntryAttr(string fullPath)
    {
        FullPath = fullPath;
    }
    public string FullPath { get; private set; }
}