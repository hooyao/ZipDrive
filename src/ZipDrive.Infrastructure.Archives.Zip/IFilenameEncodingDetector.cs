namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Detects the character encoding of ZIP filename bytes.
/// Supports per-archive detection (statistical, high accuracy) and
/// per-entry detection (fallback for mixed-encoding archives).
/// </summary>
public interface IFilenameEncodingDetector
{
    /// <summary>
    /// Detects encoding from multiple filename byte arrays (per-archive).
    /// Concatenates all bytes for better statistical accuracy.
    /// </summary>
    /// <param name="filenameByteArrays">Raw filename bytes from non-UTF8 ZIP entries.</param>
    /// <returns>Detection result with encoding and confidence, or null if detection fails.</returns>
    DetectionResult? DetectEncoding(IReadOnlyList<byte[]> filenameByteArrays);

    /// <summary>
    /// Detects encoding from a single filename byte array (per-entry fallback).
    /// Less accurate than per-archive detection due to limited data.
    /// </summary>
    /// <param name="filenameBytes">Raw filename bytes from a single ZIP entry.</param>
    /// <returns>Detection result with encoding and confidence, or null if detection fails.</returns>
    DetectionResult? DetectSingleEntry(byte[] filenameBytes);
}
