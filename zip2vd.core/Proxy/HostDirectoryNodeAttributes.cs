namespace zip2vd.core.Proxy;

public class HostDirectoryNodeAttributes: AbstractNodeAttributes
{
    public HostDirectoryNodeAttributes(string hostAbsolutePath)
    {
        HostAbsolutePath = hostAbsolutePath;
    }
    public string HostAbsolutePath { get; init; }

}