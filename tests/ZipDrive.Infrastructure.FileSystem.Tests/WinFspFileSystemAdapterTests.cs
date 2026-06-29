using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WinFsp.Native;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Exceptions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.FileSystem.Tests;

[SupportedOSPlatform("windows")]
public class WinFspFileSystemAdapterTests
{
    [Fact]
    public async Task OpenFile_ExistingFile_ReturnsMetadataAndCachesContext()
    {
        var file = FileInfo("readme.txt", "archive.zip/readme.txt", size: 123);
        var adapter = CreateAdapter(new FakeVirtualFileSystem { FileInfo = _ => file });
        var info = new FileOperationInfo();

        CreateResult result = await adapter.OpenFile("archive.zip/readme.txt", 0, 0, info, CancellationToken.None);

        result.Status.Should().Be(NtStatus.Success);
        result.FileInfo.FileSize.Should().Be(123UL);
        (result.FileInfo.FileAttributes & 0x00000001u).Should().Be(0x00000001u); // READONLY
        (result.FileInfo.FileAttributes & 0x00000080u).Should().Be(0x00000080u); // NORMAL
        info.Context.Should().Be(file);
        info.IsDirectory.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-file", typeof(VfsFileNotFoundException), nameof(NtStatus.ObjectNameNotFound))]
    [InlineData("missing-dir", typeof(VfsDirectoryNotFoundException), nameof(NtStatus.ObjectPathNotFound))]
    [InlineData("denied", typeof(VfsAccessDeniedException), nameof(NtStatus.AccessDenied))]
    public async Task OpenFile_MapsVfsExceptionsToNtStatus(string path, Type exceptionType, string statusName)
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            FileInfo = p => throw CreateVfsException(exceptionType, p)
        });

        CreateResult result = await adapter.OpenFile(path, 0, 0, new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be((int)typeof(NtStatus).GetField(statusName)!.GetValue(null)!);
    }

    [Fact]
    public async Task OpenFile_ShellMetadataWithForwardSlashes_ShortCircuitsBeforeVfsLookup()
    {
        var vfs = new FakeVirtualFileSystem
        {
            FileInfo = _ => throw new InvalidOperationException("VFS should not be called for shell metadata")
        };
        var adapter = CreateAdapter(vfs);

        CreateResult result = await adapter.OpenFile("archive.zip/desktop.ini", 0, 0, new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be(NtStatus.ObjectNameNotFound);
        vfs.GetFileInfoCalls.Should().Be(0);
    }

    [Fact]
    public void GetFileSecurityByName_ShellMetadataWithBackslashes_ShortCircuitsBeforeVfsLookup()
    {
        var vfs = new FakeVirtualFileSystem
        {
            FileInfo = _ => throw new InvalidOperationException("VFS should not be called for shell metadata")
        };
        var adapter = CreateAdapter(vfs);
        byte[]? descriptor = null;

        int status = adapter.GetFileSecurityByName(@"\archive.zip\desktop.ini", out uint attributes, ref descriptor);

        status.Should().Be(NtStatus.ObjectNameNotFound);
        attributes.Should().Be(0);
        descriptor.Should().BeNull();
        vfs.GetFileInfoCalls.Should().Be(0);
    }

    [Fact]
    public async Task OpenFile_ShellMetadataShortCircuitDisabled_CallsVfsLookup()
    {
        var file = FileInfo("desktop.ini", "archive.zip/desktop.ini", size: 7);
        var vfs = new FakeVirtualFileSystem { FileInfo = _ => file };
        var adapter = CreateAdapter(vfs, new MountSettings { ShortCircuitShellMetadata = false });

        CreateResult result = await adapter.OpenFile("archive.zip/desktop.ini", 0, 0, new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be(NtStatus.Success);
        result.FileInfo.FileSize.Should().Be(7UL);
        vfs.GetFileInfoCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReadFile_CopiesOnlyBytesReadAndCapsToDestinationLength()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            ReadFile = (_, buffer, offset) =>
            {
                offset.Should().Be(5);
                buffer.Length.Should().BeGreaterThanOrEqualTo(4);
                buffer[0] = 1;
                buffer[1] = 2;
                buffer[2] = 3;
                buffer[3] = 4;
                if (buffer.Length > 4)
                    buffer[4] = 99;
                return 4;
            }
        });
        byte[] destination = [0, 0, 0];

        ReadResult result = await adapter.ReadFile("archive.zip/file.bin", destination, 5, new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be(NtStatus.Success);
        result.BytesTransferred.Should().Be(3);
        destination.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ReadFile_ZeroBytesRead_ReturnsEndOfFile()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem { ReadFile = (_, _, _) => 0 });

        ReadResult result = await adapter.ReadFile("archive.zip/file.bin", new byte[8], 100, new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be(NtStatus.EndOfFile);
        result.BytesTransferred.Should().Be(0);
    }

    [Fact]
    public async Task PrepareDirectoryEntries_FiltersSortsAndAppliesMarker()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            DirectoryEntries = _ =>
            [
                FileInfo("zeta.bin", "zeta.bin"),
                FileInfo("beta.txt", "beta.txt"),
                FileInfo("Alpha.txt", "Alpha.txt"),
                FileInfo("gamma.txt", "gamma.txt"),
            ]
        });

        IReadOnlyList<VfsFileInfo> entries = await adapter.GuardedPrepareDirectoryEntriesAsync("", "*.txt", "beta.txt");

        entries.Select(e => e.Name).Should().Equal("gamma.txt");
    }

    [Fact]
    public async Task PrepareDirectoryEntries_WithoutMarker_ReturnsStableOrdinalIgnoreCaseOrder()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            DirectoryEntries = _ =>
            [
                FileInfo("zeta.bin", "zeta.bin"),
                FileInfo("Alpha.bin", "Alpha.bin"),
                FileInfo("beta.bin", "beta.bin"),
            ]
        });

        IReadOnlyList<VfsFileInfo> entries = await adapter.GuardedPrepareDirectoryEntriesAsync("");

        entries.Select(e => e.Name).Should().Equal("Alpha.bin", "beta.bin", "zeta.bin");
    }

    [Fact]
    public void GetDirInfoByName_FindsEntryCaseInsensitively()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            DirectoryEntries = _ => [FileInfo("Readme.TXT", "archive.zip/Readme.TXT", size: 9)]
        });

        int status = adapter.GetDirInfoByName("archive.zip", "readme.txt", out FspDirInfo dirInfo, new FileOperationInfo());

        status.Should().Be(NtStatus.Success);
        dirInfo.FileInfo.FileSize.Should().Be(9UL);
    }

    [Fact]
    public void GetDirInfoByName_MissingEntry_ReturnsObjectNameNotFound()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem
        {
            DirectoryEntries = _ => [FileInfo("present.txt", "archive.zip/present.txt")]
        });

        int status = adapter.GetDirInfoByName("archive.zip", "missing.txt", out FspDirInfo dirInfo, new FileOperationInfo());

        status.Should().Be(NtStatus.ObjectNameNotFound);
        dirInfo.Should().Be(default(FspDirInfo));
    }

    [Fact]
    public void Init_EnablesPatternPassingAndReadOnlyNtfsSemantics()
    {
        var adapter = CreateAdapter(new FakeVirtualFileSystem());
        using var host = new FileSystemHost(adapter);

        int status = adapter.Init(host);

        status.Should().Be(NtStatus.Success);
        host.PassQueryDirectoryPattern.Should().BeTrue();
        host.ReadOnlyVolume.Should().BeTrue();
        host.FileSystemName.Should().Be("NTFS");
    }

    [Fact]
    public async Task FlushFileBuffers_WithFileNameFetchesMetadataWhenContextMissing()
    {
        var file = FileInfo("readme.txt", "archive.zip/readme.txt", size: 456);
        var adapter = CreateAdapter(new FakeVirtualFileSystem { FileInfo = _ => file });

        FsResult result = await adapter.FlushFileBuffers("archive.zip/readme.txt", new FileOperationInfo(), CancellationToken.None);

        result.Status.Should().Be(NtStatus.Success);
        result.FileInfo.FileSize.Should().Be(456UL);
    }

    private static WinFspFileSystemAdapter CreateAdapter(FakeVirtualFileSystem vfs, MountSettings? mountSettings = null)
        => new(vfs, Options.Create(mountSettings ?? new MountSettings()), NullLogger<WinFspFileSystemAdapter>.Instance);

    private static VfsFileInfo FileInfo(string name, string fullPath, long size = 0, bool isDirectory = false)
    {
        DateTime timestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        return new VfsFileInfo
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = isDirectory,
            SizeBytes = size,
            CreationTimeUtc = timestamp,
            LastAccessTimeUtc = timestamp,
            LastWriteTimeUtc = timestamp,
            Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal
        };
    }

    private static Exception CreateVfsException(Type exceptionType, string path)
    {
        if (exceptionType == typeof(VfsFileNotFoundException))
            return new VfsFileNotFoundException(path);
        if (exceptionType == typeof(VfsDirectoryNotFoundException))
            return new VfsDirectoryNotFoundException(path);
        if (exceptionType == typeof(VfsAccessDeniedException))
            return new VfsAccessDeniedException(path);

        throw new ArgumentOutOfRangeException(nameof(exceptionType));
    }

    private sealed class FakeVirtualFileSystem : IVirtualFileSystem
    {
        public Func<string, VfsFileInfo> FileInfo { get; init; } = path => WinFspFileSystemAdapterTests.FileInfo(Path.GetFileName(path), path);
        public Func<string, IReadOnlyList<VfsFileInfo>> DirectoryEntries { get; init; } = _ => [];
        public Func<string, byte[], long, int> ReadFile { get; init; } = (_, _, _) => 0;

        public int GetFileInfoCalls { get; private set; }

        public bool IsMounted => true;
        public event EventHandler<bool>? MountStateChanged { add { } remove { } }

        public Task MountAsync(VfsMountOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> MountSingleFileAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task UnmountAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
        {
            GetFileInfoCalls++;
            return Task.FromResult(FileInfo(path));
        }

        public Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(DirectoryEntries(path));

        public Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken cancellationToken = default)
            => Task.FromResult(ReadFile(path, buffer, offset));

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public VfsVolumeInfo GetVolumeInfo() => new()
        {
            VolumeLabel = "ZipDrive",
            FileSystemName = "NTFS",
            TotalBytes = 1024,
            FreeBytes = 0,
            IsReadOnly = true
        };
    }
}
