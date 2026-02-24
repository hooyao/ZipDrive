namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// A read-only stream that exposes a bounded region of an underlying stream.
/// </summary>
/// <remarks>
/// <para>
/// This stream is used to limit reads to a specific range of bytes within
/// a larger stream, such as the compressed data region within a ZIP file.
/// </para>
/// <para>
/// <strong>Key Characteristics:</strong>
/// <list type="bullet">
/// <item>Read-only (Write, SetLength throw <see cref="NotSupportedException"/>)</item>
/// <item>Supports seeking within the bounded region</item>
/// <item>Position is relative to the start of the bounded region</item>
/// <item>Does NOT own the underlying stream (unless <c>leaveOpen</c> is false)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Usage Example:</strong>
/// <code>
/// // Read only the compressed data portion of a ZIP entry
/// var subStream = new SubStream(fileStream, compressedSize, leaveOpen: true);
/// using var deflateStream = new DeflateStream(subStream, CompressionMode.Decompress);
/// </code>
/// </para>
/// </remarks>
public sealed class SubStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _startPosition;
    private readonly long _length;
    private readonly bool _leaveOpen;

    private long _position;
    private bool _disposed;

    /// <summary>
    /// Creates a new SubStream starting at the current position of the base stream.
    /// </summary>
    /// <param name="baseStream">The underlying stream to read from.</param>
    /// <param name="length">Maximum number of bytes that can be read.</param>
    /// <param name="leaveOpen">
    /// If true, the base stream is not disposed when this stream is disposed.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="baseStream"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="length"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseStream"/> does not support reading.
    /// </exception>
    public SubStream(Stream baseStream, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        if (!baseStream.CanRead)
            throw new ArgumentException("Base stream must support reading.", nameof(baseStream));

        _baseStream = baseStream;
        _startPosition = baseStream.Position;
        _length = length;
        _leaveOpen = leaveOpen;
        _position = 0;
    }

    /// <summary>
    /// Creates a new SubStream starting at a specific position.
    /// </summary>
    /// <param name="baseStream">The underlying stream to read from.</param>
    /// <param name="startPosition">Absolute position in the base stream where this region starts.</param>
    /// <param name="length">Maximum number of bytes that can be read.</param>
    /// <param name="leaveOpen">
    /// If true, the base stream is not disposed when this stream is disposed.
    /// </param>
    public SubStream(Stream baseStream, long startPosition, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (startPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(startPosition), "Start position cannot be negative.");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        if (!baseStream.CanRead)
            throw new ArgumentException("Base stream must support reading.", nameof(baseStream));

        _baseStream = baseStream;
        _startPosition = startPosition;
        _length = length;
        _leaveOpen = leaveOpen;
        _position = 0;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed && _baseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Position must be between 0 and {_length}.");
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBufferArguments(buffer, offset, count);

        // Limit read to remaining bytes in the bounded region
        long remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(count, remaining);

        // Seek to correct position in base stream
        long targetPosition = _startPosition + _position;
        if (_baseStream.Position != targetPosition)
            _baseStream.Seek(targetPosition, SeekOrigin.Begin);

        int bytesRead = _baseStream.Read(buffer, offset, toRead);
        _position += bytesRead;

        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBufferArguments(buffer, offset, count);

        long remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(count, remaining);

        long targetPosition = _startPosition + _position;
        if (_baseStream.Position != targetPosition)
            _baseStream.Seek(targetPosition, SeekOrigin.Begin);

        int bytesRead = await _baseStream.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken)
            .ConfigureAwait(false);
        _position += bytesRead;

        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);

        long targetPosition = _startPosition + _position;
        if (_baseStream.Position != targetPosition)
            _baseStream.Seek(targetPosition, SeekOrigin.Begin);

        int bytesRead = await _baseStream.ReadAsync(buffer[..toRead], cancellationToken)
            .ConfigureAwait(false);
        _position += bytesRead;

        return bytesRead;
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long remaining = _length - _position;
        if (remaining <= 0)
            return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);

        long targetPosition = _startPosition + _position;
        if (_baseStream.Position != targetPosition)
            _baseStream.Seek(targetPosition, SeekOrigin.Begin);

        int bytesRead = _baseStream.Read(buffer[..toRead]);
        _position += bytesRead;

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Resulting position {newPosition} is outside the bounded region [0, {_length}].");

        _position = newPosition;
        return _position;
    }

    public override void Flush()
    {
        // No-op for read-only stream
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("SubStream is read-only.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("SubStream is read-only.");
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && !_leaveOpen)
            {
                _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (!_leaveOpen)
            {
                await _baseStream.DisposeAsync().ConfigureAwait(false);
            }
            _disposed = true;
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private static new void ValidateBufferArguments(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
        if (offset + count > buffer.Length)
            throw new ArgumentException("Offset and count exceed buffer length.");
    }
}
