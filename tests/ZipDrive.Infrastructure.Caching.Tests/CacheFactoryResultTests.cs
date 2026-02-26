using FluentAssertions;
using ZipDrive.Infrastructure.Caching;

namespace ZipDrive.Infrastructure.Caching.Tests;

public sealed class CacheFactoryResultTests
{
    [Fact]
    public async Task DisposeAsync_WithIDisposableValue_DisposesValue()
    {
        DisposableStub stub = new();
        CacheFactoryResult<DisposableStub> result = new()
        {
            Value = stub,
            SizeBytes = 100
        };

        await result.DisposeAsync();

        stub.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithIAsyncDisposableValue_DisposesValueAsync()
    {
        AsyncDisposableStub stub = new();
        CacheFactoryResult<AsyncDisposableStub> result = new()
        {
            Value = stub,
            SizeBytes = 100
        };

        await result.DisposeAsync();

        stub.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithIAsyncDisposableValue_PrefersAsyncDispose()
    {
        DualDisposableStub stub = new();
        CacheFactoryResult<DualDisposableStub> result = new()
        {
            Value = stub,
            SizeBytes = 100
        };

        await result.DisposeAsync();

        stub.AsyncDisposed.Should().BeTrue();
        stub.SyncDisposed.Should().BeFalse("IAsyncDisposable should be preferred over IDisposable");
    }

    [Fact]
    public async Task DisposeAsync_WithOnDisposed_InvokesCallbackAfterValueDisposal()
    {
        DisposableStub stub = new();
        bool callbackInvoked = false;
        bool valueWasDisposedBeforeCallback = false;

        CacheFactoryResult<DisposableStub> result = new()
        {
            Value = stub,
            SizeBytes = 100,
            OnDisposed = () =>
            {
                valueWasDisposedBeforeCallback = stub.Disposed;
                callbackInvoked = true;
                return ValueTask.CompletedTask;
            }
        };

        await result.DisposeAsync();

        callbackInvoked.Should().BeTrue();
        valueWasDisposedBeforeCallback.Should().BeTrue("OnDisposed should be called after Value disposal");
    }

    [Fact]
    public async Task DisposeAsync_WithNonDisposableValue_IsNoOp()
    {
        CacheFactoryResult<string> result = new()
        {
            Value = "hello",
            SizeBytes = 5
        };

        // Should not throw
        await result.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithNonDisposableValueAndOnDisposed_InvokesCallback()
    {
        bool callbackInvoked = false;
        CacheFactoryResult<string> result = new()
        {
            Value = "hello",
            SizeBytes = 5,
            OnDisposed = () =>
            {
                callbackInvoked = true;
                return ValueTask.CompletedTask;
            }
        };

        await result.DisposeAsync();

        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_OnlyDisposesOnce()
    {
        DisposableStub stub = new();
        int callbackCount = 0;

        CacheFactoryResult<DisposableStub> result = new()
        {
            Value = stub,
            SizeBytes = 100,
            OnDisposed = () =>
            {
                callbackCount++;
                return ValueTask.CompletedTask;
            }
        };

        await result.DisposeAsync();
        await result.DisposeAsync();

        stub.DisposeCount.Should().Be(1);
        callbackCount.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Test stubs
    // ═══════════════════════════════════════════════════════════════════

    private sealed class DisposableStub : IDisposable
    {
        public bool Disposed => DisposeCount > 0;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class AsyncDisposableStub : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DualDisposableStub : IAsyncDisposable, IDisposable
    {
        public bool AsyncDisposed { get; private set; }
        public bool SyncDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            SyncDisposed = true;
        }
    }
}
