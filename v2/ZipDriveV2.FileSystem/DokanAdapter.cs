using ZipDriveV2.Core;

namespace ZipDriveV2.FileSystem;

public sealed class DokanAdapter
{
    private readonly IArchiveRegistry _registry;
    private readonly IPathResolver _resolver;

    public DokanAdapter(IArchiveRegistry registry, IPathResolver resolver)
    {
        _registry = registry;
        _resolver = resolver;
    }

    // Stub methods simulating mapping of filesystem operations
    public object? Open(string path)
    {
        var (key, inner, status) = _resolver.Split(path);
        return status switch
        {
            PathResolutionStatus.Root => _registry.List(),
            _ => null
        };
    }
}
