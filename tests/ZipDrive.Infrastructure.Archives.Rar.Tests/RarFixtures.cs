namespace ZipDrive.Infrastructure.Archives.Rar.Tests;

/// <summary>
/// Provides minimal RAR archive header bytes for testing.
/// These are hand-crafted byte sequences that pass SharpCompress IsRarFile validation.
/// They contain only the signature and main archive header -- no file entries.
/// </summary>
internal static class RarFixtures
{
    /// <summary>RAR5 signature (8 bytes): Rar!\x1A\x07\x01\x00</summary>
    internal static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    /// <summary>RAR4 signature (7 bytes): Rar!\x1A\x07\x00</summary>
    internal static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Minimal RAR5 non-solid header (15 bytes).
    /// Signature(8) + CRC32(4) + HeaderSize(1 vint=5) + HeaderType(1 vint=1) + Flags(1 vint=0)
    /// </summary>
    internal static readonly byte[] Rar5NonSolidHeader =
    [
        0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, // RAR5 signature
        0x00, 0x00, 0x00, 0x00,                           // CRC32 placeholder
        0x05,                                              // header size = 5 (vint)
        0x01,                                              // header type = 1 (main archive)
        0x00                                               // flags = 0 (non-solid)
    ];

    /// <summary>
    /// Minimal RAR5 solid header (15 bytes). Flags bit 0 set.
    /// </summary>
    internal static readonly byte[] Rar5SolidHeader =
    [
        0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, // RAR5 signature
        0x00, 0x00, 0x00, 0x00,                           // CRC32 placeholder
        0x05,                                              // header size = 5 (vint)
        0x01,                                              // header type = 1 (main archive)
        0x01                                               // flags = 1 (solid)
    ];

    /// <summary>
    /// Minimal RAR4 non-solid header (21 bytes).
    /// Signature(7) + MarkerBlock(7) + MainArchiveHeader(7 with flags=0x0000)
    /// </summary>
    internal static readonly byte[] Rar4NonSolidHeader =
    [
        0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00,        // RAR4 signature
        0x21, 0x1C, 0x72, 0x00, 0x1D, 0x07, 0x00,        // marker block (type 0x72)
        0x00, 0x00, 0x73, 0x00, 0x00, 0x0D, 0x00         // main archive header (type 0x73, flags=0)
    ];

    /// <summary>
    /// Minimal RAR4 solid header (21 bytes). Flags = 0x0008 (solid bit).
    /// </summary>
    internal static readonly byte[] Rar4SolidHeader =
    [
        0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00,        // RAR4 signature
        0x21, 0x1C, 0x72, 0x00, 0x1D, 0x07, 0x00,        // marker block (type 0x72)
        0x00, 0x00, 0x73, 0x08, 0x00, 0x0D, 0x00         // main archive header (type 0x73, flags=0x0008)
    ];

    /// <summary>
    /// Creates a temporary file with the given content and returns its path.
    /// The file is created in the system temp directory.
    /// </summary>
    internal static string WriteTempFile(byte[] content, string extension = ".rar")
    {
        string path = Path.Combine(Path.GetTempPath(), $"zipdrivetest_{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Deletes a temp file if it exists, swallowing any exceptions.
    /// </summary>
    internal static void CleanupTempFile(string? path)
    {
        if (path != null)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
