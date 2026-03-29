using System.Collections.Concurrent;

namespace ZipDrive.Application.Services;

/// <summary>
/// Disposable guard that pairs TryEnter/Exit on an ArchiveNode.
/// Use with 'using' to ensure Exit is always called.
/// </summary>
internal readonly struct ArchiveGuard : IDisposable
{
    private readonly ArchiveNode? _node;

    private ArchiveGuard(ArchiveNode node) => _node = node;

    /// <summary>
    /// Attempts to enter the archive node. Returns false if draining or not found.
    /// On success, dispose the guard to call Exit.
    /// </summary>
    public static bool TryEnter(
        ConcurrentDictionary<string, ArchiveNode> nodes,
        string archiveKey,
        out ArchiveGuard guard)
    {
        guard = default;
        if (!nodes.TryGetValue(archiveKey, out var node) || !node.TryEnter())
            return false;
        guard = new ArchiveGuard(node);
        return true;
    }

    public void Dispose() => _node?.Exit();
}
