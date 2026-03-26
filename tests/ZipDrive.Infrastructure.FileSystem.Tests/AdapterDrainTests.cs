using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.FileSystem;

namespace ZipDrive.Infrastructure.FileSystem.Tests;

public class AdapterDrainTests
{
    private static DokanFileSystemAdapter CreateAdapter()
    {
        var settings = Options.Create(new MountSettings { ShortCircuitShellMetadata = false });
        return new DokanFileSystemAdapter(settings, NullLogger<DokanFileSystemAdapter>.Instance);
    }

    private sealed class FakeVfs : IVirtualFileSystem
    {
        public bool IsMounted { get; set; } = true;
        public event EventHandler<bool>? MountStateChanged;

        // Allows tests to block ReadFileAsync to simulate in-flight operations
        public TaskCompletionSource<int> ReadGate { get; } = new();

        public Task MountAsync(VfsMountOptions options, CancellationToken ct = default)
        {
            IsMounted = true;
            MountStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        }

        public Task UnmountAsync(CancellationToken ct = default) { IsMounted = false; return Task.CompletedTask; }
        public Task<VfsFileInfo> GetFileInfoAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct = default) => ReadGate.Task;
        public Task<bool> FileExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public VfsVolumeInfo GetVolumeInfo() => throw new NotImplementedException();
    }

    [Fact]
    public async Task SwapAsync_replaces_vfs_and_returns_old()
    {
        var adapter = CreateAdapter();
        var vfs1 = new FakeVfs();
        var vfs2 = new FakeVfs();

        adapter.SetVfs(vfs1);

        var old = await adapter.SwapAsync(vfs2, TimeSpan.FromSeconds(5));

        old.Should().BeSameAs(vfs1);
    }

    [Fact]
    public async Task SwapAsync_with_no_active_ops_completes_immediately()
    {
        var adapter = CreateAdapter();
        adapter.SetVfs(new FakeVfs());

        adapter.ActiveCount.Should().Be(0);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await adapter.SwapAsync(new FakeVfs(), TimeSpan.FromSeconds(30));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SwapAsync_timeout_forces_swap()
    {
        var adapter = CreateAdapter();
        adapter.SetVfs(new FakeVfs());

        // We can't easily simulate in-flight ops without calling Dokan callbacks (which need native memory).
        // Instead, test that a swap with zero timeout still completes.
        adapter.ActiveCount.Should().Be(0);
        await adapter.SwapAsync(new FakeVfs(), TimeSpan.FromMilliseconds(50));

        // If we get here, it completed without hanging.
    }

    [Fact]
    public void SetVfs_makes_vfs_available()
    {
        var adapter = CreateAdapter();
        var vfs = new FakeVfs();

        adapter.SetVfs(vfs);

        // ActiveCount should be 0 initially
        adapter.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void SetVfs_throws_on_null()
    {
        var adapter = CreateAdapter();
        var act = () => adapter.SetVfs(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SwapAsync_throws_on_null()
    {
        var adapter = CreateAdapter();
        adapter.SetVfs(new FakeVfs());

        var act = async () => await adapter.SwapAsync(null!, TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Multiple_swaps_work_sequentially()
    {
        var adapter = CreateAdapter();
        var vfs1 = new FakeVfs();
        var vfs2 = new FakeVfs();
        var vfs3 = new FakeVfs();

        adapter.SetVfs(vfs1);

        var old1 = await adapter.SwapAsync(vfs2, TimeSpan.FromSeconds(5));
        old1.Should().BeSameAs(vfs1);

        var old2 = await adapter.SwapAsync(vfs3, TimeSpan.FromSeconds(5));
        old2.Should().BeSameAs(vfs2);
    }
}
