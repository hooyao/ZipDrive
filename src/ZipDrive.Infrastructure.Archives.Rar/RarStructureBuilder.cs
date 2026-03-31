using System.Text;
using KTrie;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives.Rar;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.Archives.Rar;

/// <summary>
/// RAR-specific structure builder. Parses a RAR archive using SharpCompress
/// and produces an ArchiveStructure with format-agnostic ArchiveEntryInfo entries.
///
/// Solid RAR archives are detected by ProbeAsync and rejected before reaching BuildAsync.
/// If a solid archive reaches BuildAsync (defense-in-depth), a synthetic warning structure
/// is returned instead.
/// </summary>
public sealed class RarStructureBuilder : IArchiveStructureBuilder
{
    private const long BytesPerEntry = 96;
    private const long BaseOverhead = 1024;
    internal const string UnsupportedWarningFileName = "NOT_SUPPORTED_WARNING.txt";
    // Suffix constant lives in ArchiveProbeResult.UnsupportedFolderSuffix (Domain layer)

    internal static readonly byte[] SolidWarningContent = Encoding.UTF8.GetBytes(
"""
This RAR archive uses solid compression and cannot be mounted by ZipDrive.

Solid RAR archives compress all files as a single continuous data stream.
Extracting any single file requires decompressing every preceding file first,
which makes random-access reads impractical for a virtual filesystem.

To access this archive's contents through ZipDrive, re-create it without
solid compression:

    WinRAR:  Uncheck "Create solid archive" in compression settings
    CLI:     rar a -s- output.rar input_files/

Alternatively, extract the archive manually with WinRAR or 7-Zip.

To hide unsupported archives from the virtual drive entirely, set:

    "Mount": {
        "HideUnsupportedArchives": true
    }

in appsettings.jsonc and restart ZipDrive.
""");

    private readonly ILogger<RarStructureBuilder> _logger;

    public string FormatId => "rar";
    public IReadOnlyList<string> SupportedExtensions => [".rar"];

    public RarStructureBuilder(ILogger<RarStructureBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lightweight probe: reads only the file header to detect solid RAR archives.
    /// Solid archives cannot be efficiently mounted because extracting any single file
    /// requires decompressing all preceding files.
    /// </summary>
    public Task<ArchiveProbeResult> ProbeAsync(
        string absolutePath, CancellationToken cancellationToken = default)
    {
        bool isSolid = RarSignature.IsSolid(absolutePath);
        ArchiveProbeResult result = isSolid
            ? new ArchiveProbeResult(false, "Solid RAR archives are not supported")
            : new ArchiveProbeResult(true);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Parses all entries in the RAR archive and produces an ArchiveStructure.
    /// SharpCompress RarArchive.OpenArchive is synchronous, so this wraps it in a Task.
    /// </summary>
    public Task<ArchiveStructure> BuildAsync(
        string archiveKey, string absolutePath, CancellationToken cancellationToken = default)
    {
        using var archive = RarArchive.OpenArchive(absolutePath);

        // Defense-in-depth: solid should be caught by ProbeAsync before reaching here.
        if (archive.IsSolid)
        {
            _logger.LogWarning(
                "Solid RAR reached BuildAsync (should have been caught by ProbeAsync): {Path}",
                absolutePath);
            return Task.FromResult(
                BuildSolidWarningStructure(archiveKey + ArchiveProbeResult.UnsupportedFolderSuffix, absolutePath));
        }

        var trie = new TrieDictionary<ArchiveEntryInfo>();
        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key = NormalizePath(entry.Key ?? "");
            if (string.IsNullOrEmpty(key)) continue;

            // SharpCompress sets IsDirectory but key may not end with /
            string entryKey = entry.IsDirectory
                ? (key.EndsWith('/') ? key : key + "/")
                : key.TrimEnd('/');

            trie[entryKey] = new ArchiveEntryInfo
            {
                UncompressedSize = entry.Size,
                IsDirectory = entry.IsDirectory,
                LastModified = entry.LastModifiedTime ?? DateTime.MinValue,
                Attributes = entry.IsDirectory
                    ? FileAttributes.Directory | FileAttributes.ReadOnly
                    : FileAttributes.ReadOnly,
                Checksum = GetSafeCrc(entry),
            };

            if (!entry.IsDirectory)
            {
                totalUncompressed += entry.Size;
                totalCompressed += entry.CompressedSize;
            }
        }

        DirectorySynthesizer.SynthesizeParentDirectories(trie);

        var structure = new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
            FormatId = "rar",
            BuiltAt = DateTimeOffset.UtcNow,
            TotalUncompressedSize = totalUncompressed,
            TotalCompressedSize = totalCompressed,
            EstimatedMemoryBytes = BaseOverhead + trie.Count * BytesPerEntry,
        };

        _logger.LogInformation("Built RAR structure for {Key}: {Count} entries", archiveKey, trie.Count);
        return Task.FromResult(structure);
    }

    /// <summary>
    /// Creates a synthetic structure with a single warning file for solid archives.
    /// This allows the archive to appear on the virtual drive with an explanatory message.
    /// </summary>
    internal static ArchiveStructure BuildSolidWarningStructure(string archiveKey, string absolutePath)
    {
        var trie = new TrieDictionary<ArchiveEntryInfo>
        {
            [UnsupportedWarningFileName] = new ArchiveEntryInfo
            {
                UncompressedSize = SolidWarningContent.Length,
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Attributes = FileAttributes.ReadOnly,
            }
        };

        return new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
            FormatId = "rar",
            BuiltAt = DateTimeOffset.UtcNow,
            TotalUncompressedSize = SolidWarningContent.Length,
            TotalCompressedSize = 0,
            EstimatedMemoryBytes = BaseOverhead + BytesPerEntry,
        };
    }

    /// <summary>
    /// Safely reads CRC from a RAR entry. SharpCompress throws ArgumentNullException
    /// on entries without CRC (e.g., directories in some RAR versions).
    /// </summary>
    private static uint GetSafeCrc(SharpCompress.Archives.IArchiveEntry entry)
    {
        try { return (uint)entry.Crc; }
        catch { return 0; }
    }

    /// <summary>
    /// Normalizes an archive entry path: backslash to forward slash, strip leading slash.
    /// </summary>
    internal static string NormalizePath(string fileName)
    {
        string normalized = fileName.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            normalized = normalized[1..];
        return normalized;
    }
}
