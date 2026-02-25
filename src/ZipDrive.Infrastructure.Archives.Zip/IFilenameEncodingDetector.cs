using System.Text;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Detects the character encoding of ZIP filename bytes.
/// Handles the full detection chain internally, including fallback.
/// </summary>
public interface IFilenameEncodingDetector
{
    /// <summary>
    /// Detects encoding from multiple filename byte arrays (per-archive).
    /// Returns the detected encoding if confidence is above threshold, or null
    /// to signal that per-entry detection should be used instead.
    /// </summary>
    /// <param name="filenameByteArrays">Raw filename bytes from non-UTF8 ZIP entries.</param>
    /// <returns>Detected encoding for the archive, or null if per-entry detection is needed.</returns>
    Encoding? DetectArchiveEncoding(IReadOnlyList<byte[]> filenameByteArrays);

    /// <summary>
    /// Resolves the encoding for a single filename byte array.
    /// Always returns a usable encoding — falls back through the detection chain
    /// (UtfUnknown → system OEM → configured fallback).
    /// </summary>
    /// <param name="filenameBytes">Raw filename bytes from a single ZIP entry.</param>
    /// <returns>The encoding to use. Never returns null.</returns>
    Encoding ResolveEntryEncoding(byte[] filenameBytes);
}
