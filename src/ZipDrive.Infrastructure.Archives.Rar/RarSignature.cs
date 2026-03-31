using System.Buffers.Binary;

namespace ZipDrive.Infrastructure.Archives.Rar;

/// <summary>
/// Binary detection for RAR archive versions and solid compression flag.
/// Reads only the header bytes (no SharpCompress dependency) for fast probing.
/// </summary>
internal static class RarSignature
{
    private static ReadOnlySpan<byte> Rar5Magic => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static ReadOnlySpan<byte> Rar4Magic => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Detects the RAR version from the file header bytes.
    /// Returns 5 for RAR5, 4 for RAR4, 0 for unknown.
    /// </summary>
    internal static int DetectVersion(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(Rar5Magic)) return 5;
        if (header.Length >= 7 && header[..7].SequenceEqual(Rar4Magic)) return 4;
        return 0;
    }

    /// <summary>
    /// Determines if a RAR archive uses solid compression by opening it with SharpCompress.
    /// SharpCompress correctly parses the solid flag across RAR4 and RAR5 formats.
    /// This is fast (~1ms) as it only reads headers, not entry data.
    /// </summary>
    internal static bool IsSolid(string filePath)
    {
        try
        {
            using var archive = SharpCompress.Archives.Rar.RarArchive.OpenArchive(filePath);
            return archive.IsSolid;
        }
        catch
        {
            return false; // If we can't open it, treat as non-solid (will fail at BuildAsync)
        }
    }

}
