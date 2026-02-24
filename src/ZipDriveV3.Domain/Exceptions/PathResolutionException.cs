namespace ZipDriveV3.Domain.Exceptions;

public class PathResolutionException : Exception
{
    public string VirtualPath { get; }

    public PathResolutionException(string virtualPath, string message)
        : base($"Path resolution failed for '{virtualPath}': {message}")
    {
        VirtualPath = virtualPath;
    }
}
