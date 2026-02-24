using ZipDrive.Domain.Models;
using ZipDrive.Infrastructure.Archives.Zip.Formats;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Low-level ZIP archive reader with streaming Central Directory enumeration.
/// Parses ZIP format structures without loading the entire CD into memory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key Features:</strong>
/// <list type="bullet">
/// <item>Streaming Central Directory enumeration via <see cref="IAsyncEnumerable{T}"/></item>
/// <item>ZIP64 support for large files and archives</item>
/// <item>Single-seek extraction (Local Header → compressed data)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong>
/// Instances are NOT thread-safe. Create one reader per thread/operation or use external
/// synchronization. The underlying Stream is accessed sequentially during parsing.
/// </para>
/// <para>
/// <strong>Stream Requirements:</strong>
/// <list type="bullet">
/// <item>Must support seeking (<c>CanSeek = true</c>)</item>
/// <item>Must support reading (<c>CanRead = true</c>)</item>
/// <item>Should be opened with <c>FileShare.Read</c> for concurrent access</item>
/// </list>
/// </para>
/// <para>
/// <strong>Usage Example:</strong>
/// <code>
/// await using var reader = new ZipReader(stream);
/// var eocd = await reader.ReadEocdAsync();
///
/// // Streaming enumeration - one entry at a time
/// await foreach (var entry in reader.StreamCentralDirectoryAsync(eocd))
/// {
///     Console.WriteLine($"{entry.FileName}: {entry.UncompressedSize} bytes");
/// }
/// </code>
/// </para>
/// </remarks>
public interface IZipReader : IAsyncDisposable
{
    /// <summary>
    /// Reads and parses the End of Central Directory record.
    /// Handles both standard ZIP and ZIP64 formats transparently.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed EOCD with Central Directory location and entry count.</returns>
    /// <exception cref="Domain.Exceptions.EocdNotFoundException">
    /// EOCD signature not found (not a ZIP file or truncated).
    /// </exception>
    /// <exception cref="Domain.Exceptions.Zip64RequiredException">
    /// ZIP64 EOCD is required but not found.
    /// </exception>
    /// <exception cref="Domain.Exceptions.InvalidOffsetException">
    /// Central Directory offset exceeds file size.
    /// </exception>
    /// <exception cref="Domain.Exceptions.MultiDiskArchiveException">
    /// Archive spans multiple disks (not supported).
    /// </exception>
    Task<ZipEocd> ReadEocdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams Central Directory entries one-by-one without bulk allocation.
    /// Each iteration reads one entry from disk and yields immediately.
    /// </summary>
    /// <param name="eocd">Previously parsed EOCD (from <see cref="ReadEocdAsync"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Async enumerable of Central Directory entries.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Memory Efficiency:</strong>
    /// This method does NOT allocate a list or array for all entries.
    /// Each entry is yielded immediately after parsing, allowing the caller
    /// to process entries one at a time with minimal memory overhead.
    /// </para>
    /// <para>
    /// <strong>Cancellation:</strong>
    /// Cancellation is checked between entries. Long-running operations on
    /// huge archives can be cancelled mid-enumeration.
    /// </para>
    /// </remarks>
    /// <exception cref="Domain.Exceptions.InvalidSignatureException">
    /// Invalid CD entry signature encountered.
    /// </exception>
    /// <exception cref="Domain.Exceptions.CorruptZipException">
    /// Unexpected end of stream during parsing.
    /// </exception>
    /// <example>
    /// <code>
    /// var eocd = await reader.ReadEocdAsync();
    ///
    /// // Process entries one at a time - no bulk memory allocation
    /// await foreach (var entry in reader.StreamCentralDirectoryAsync(eocd))
    /// {
    ///     var info = ConvertToZipEntryInfo(entry);
    ///     entries[entry.FileName] = info;
    /// }
    /// </code>
    /// </example>
    IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(
        ZipEocd eocd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a decompression stream for extracting a single entry.
    /// Performs a single seek to the Local Header, reads header, returns wrapped stream.
    /// </summary>
    /// <param name="entry">Entry info (must have valid LocalHeaderOffset).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Stream that decompresses data as it's read.
    /// <list type="bullet">
    /// <item>For Store (method 0): Returns bounded <see cref="SubStream"/>.</item>
    /// <item>For Deflate (method 8): Returns <see cref="System.IO.Compression.DeflateStream"/> wrapper.</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Stream Ownership:</strong>
    /// The returned stream does NOT own the underlying file stream.
    /// The caller must dispose the returned stream when done.
    /// </para>
    /// <para>
    /// <strong>Concurrency:</strong>
    /// Each extraction operation needs its own <see cref="IZipReader"/> instance
    /// because the file stream position is modified.
    /// </para>
    /// </remarks>
    /// <exception cref="Domain.Exceptions.InvalidSignatureException">
    /// Local Header signature is invalid.
    /// </exception>
    /// <exception cref="Domain.Exceptions.UnsupportedCompressionException">
    /// Compression method is not supported.
    /// </exception>
    /// <exception cref="Domain.Exceptions.EncryptedEntryException">
    /// Entry is encrypted (password support not implemented).
    /// </exception>
    Task<Stream> OpenEntryStreamAsync(
        ZipEntryInfo entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the Local Header at a given offset.
    /// Used to calculate actual data offset (header size varies).
    /// </summary>
    /// <param name="localHeaderOffset">Byte offset to Local Header.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed Local Header.</returns>
    /// <exception cref="Domain.Exceptions.InvalidSignatureException">
    /// Local Header signature is invalid.
    /// </exception>
    /// <exception cref="Domain.Exceptions.CorruptZipException">
    /// Unexpected end of stream while reading header.
    /// </exception>
    Task<ZipLocalHeader> ReadLocalHeaderAsync(
        long localHeaderOffset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the length of the underlying stream (file size).
    /// </summary>
    long StreamLength { get; }

    /// <summary>
    /// Gets the file path of the ZIP archive, if available.
    /// </summary>
    string? FilePath { get; }
}
