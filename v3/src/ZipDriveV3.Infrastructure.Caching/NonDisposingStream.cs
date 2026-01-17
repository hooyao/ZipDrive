namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// A stream wrapper that delegates all operations to an inner stream
/// but prevents disposal of the underlying stream.
/// </summary>
/// <remarks>
/// <para>
/// This class is used by the cache to safely return streams to callers.
/// The cache maintains ownership of the underlying resources (byte arrays
/// or memory-mapped files), while callers can dispose the wrapper stream
/// without affecting cached resources.
/// </para>
/// <para>
/// <strong>Design Rationale:</strong> Prevents accidental disposal of shared
/// cache resources when multiple concurrent readers access the same cached entry.
/// </para>
/// </remarks>
internal sealed class NonDisposingStream : Stream
{
    private readonly Stream _innerStream;

    /// <summary>
    /// Initializes a new instance of the <see cref="NonDisposingStream"/> class.
    /// </summary>
    /// <param name="innerStream">The underlying stream to wrap.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="innerStream"/> is null.
    /// </exception>
    public NonDisposingStream(Stream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    }

    /// <inheritdoc />
    public override bool CanRead => _innerStream.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _innerStream.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _innerStream.CanWrite;

    /// <inheritdoc />
    public override long Length => _innerStream.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    /// <inheritdoc />
    public override void Flush() => _innerStream.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => _innerStream.Read(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
        => _innerStream.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value)
        => _innerStream.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => _innerStream.Write(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _innerStream.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _innerStream.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _innerStream.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        => _innerStream.CopyToAsync(destination, bufferSize, cancellationToken);

    /// <summary>
    /// Disposes this wrapper (but NOT the inner stream).
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    /// <remarks>
    /// <strong>Critical:</strong> This method intentionally does NOT dispose the inner stream.
    /// The cache maintains ownership of the underlying resource.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        // Intentionally do nothing - cache owns the inner stream
    }

    /// <summary>
    /// Asynchronously disposes this wrapper (but NOT the inner stream).
    /// </summary>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// <strong>Critical:</strong> This method intentionally does NOT dispose the inner stream.
    /// The cache maintains ownership of the underlying resource.
    /// </remarks>
    public override ValueTask DisposeAsync()
    {
        // Intentionally do nothing - cache owns the inner stream
        return ValueTask.CompletedTask;
    }
}
