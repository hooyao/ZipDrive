using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ZipDriveV2.Core;

public interface IArchiveProvider
{
    string FormatId { get; }
    bool CanOpen(string fileName, ReadOnlySpan<byte> headerSample);
    IArchiveSession Open(ArchiveOpenContext context);
}

public interface IArchiveSession : IAsyncDisposable
{
    ArchiveInfo Info { get; }
    IEnumerable<IArchiveEntry> Entries();
    IArchiveEntry? GetEntry(string path);
    Stream OpenRead(IArchiveEntry entry, Range? range = null, CancellationToken ct = default);
    IArchiveCapabilities Capabilities { get; }
}

public interface IArchiveEntry
{
    string Path { get; }
    string Name { get; }
    bool IsDirectory { get; }
    long Size { get; }
    DateTimeOffset? ModifiedUtc { get; }
}

public interface IArchiveCapabilities
{
    bool SupportsRandomAccess { get; }
    bool SupportsWrite { get; }
    bool SupportsPassword { get; }
}

public sealed record ArchiveInfo(string PhysicalPath, string FormatId, long Size, DateTimeOffset LastWriteTimeUtc);
public sealed record ArchiveOpenContext(string PhysicalPath);

public interface IArchiveRegistry
{
    IReadOnlyCollection<ArchiveDescriptor> List();
    ArchiveDescriptor? Get(string archiveKey);
    event EventHandler<ArchivesChangedEventArgs>? Changed;
}

public sealed record ArchiveDescriptor(string Key, string PhysicalPath, string FormatId, DateTimeOffset LastWriteTimeUtc, long Size, IArchiveCapabilities Capabilities, ArchiveState State);
public enum ArchiveState { Clean, Dirty, Faulted }

public sealed class ArchivesChangedEventArgs : EventArgs
{
    public IReadOnlyCollection<ArchiveDescriptor> Added { get; init; } = Array.Empty<ArchiveDescriptor>();
    public IReadOnlyCollection<ArchiveDescriptor> Removed { get; init; } = Array.Empty<ArchiveDescriptor>();
    public IReadOnlyCollection<ArchiveDescriptor> Updated { get; init; } = Array.Empty<ArchiveDescriptor>();
}

public interface IArchiveIndex
{
    bool IsBuilt { get; }
    void EnsureBuilt();
    IFileSystemNode? GetNode(string relativePath);
    IEnumerable<IFileSystemNode> GetChildren(string relativePath);
}

public interface IFileSystemTree
{
    IFileSystemNode? Resolve(string virtualPath);
    IEnumerable<IFileSystemNode> List(string virtualPath);
}

public interface IFileSystemNode
{
    string Name { get; }
    string FullVirtualPath { get; }
    long? Size { get; }
    bool IsDirectory { get; }
}

public interface IPathResolver
{
    (string? ArchiveKey, string InnerPath, PathResolutionStatus Status) Split(string rawPath);
}

public enum PathResolutionStatus { Root, ArchiveRoot, Entry, Invalid }

public interface IStreamOpener
{
    Stream Open(VirtualFileHandle handle, Range? range, CancellationToken ct);
}

public sealed record VirtualFileHandle(ArchiveDescriptor Archive, IArchiveEntry Entry);
