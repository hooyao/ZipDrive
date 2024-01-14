namespace zip2vd.core.Proxy;

public struct ZipEntryAttr
{
    public ZipEntryAttr(string fullPath)
    {
        FullPath = fullPath;
    }
    public string FullPath { get; private set; }
}