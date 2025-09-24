using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ZipDriveV2.Core;

namespace ZipDriveV2.Archives;

public sealed class StubZipArchiveProvider : IArchiveProvider
{
    public string FormatId => "zip";
    public bool CanOpen(string fileName, ReadOnlySpan<byte> headerSample) => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public IArchiveSession Open(ArchiveOpenContext context) => new StubSession(context.PhysicalPath);

    private sealed class StubSession : IArchiveSession
    {
        private readonly string _path;
        public StubSession(string path) { _path = path; }
        public ArchiveInfo Info => new ArchiveInfo(_path, "zip", 0, DateTimeOffset.UtcNow);
        public IArchiveCapabilities Capabilities => new Caps();
        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }
        public IEnumerable<IArchiveEntry> Entries() { yield break; }
        public IArchiveEntry? GetEntry(string path) => null;
        public Stream OpenRead(IArchiveEntry entry, Range? range = null, CancellationToken ct = default) => Stream.Null;
    }

    private sealed class Caps : IArchiveCapabilities
    {
        public bool SupportsRandomAccess => true;
        public bool SupportsWrite => false;
        public bool SupportsPassword => false;
    }
}
