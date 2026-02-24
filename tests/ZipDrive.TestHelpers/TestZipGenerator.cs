using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZipDrive.TestHelpers;

/// <summary>
/// Generates test ZIP files with embedded manifests for verification.
/// File content is deterministic (seeded by file path) for reproducibility.
/// </summary>
public static class TestZipGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Generates a single test ZIP file with the given profile.
    /// </summary>
    public static async Task GenerateZipAsync(string outputPath, ZipProfile profile, int seed = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        List<(string path, long size)> fileSpecs = GetFileSpecs(profile, seed);
        ZipManifest manifest = new()
        {
            Profile = profile.ToString(),
            GeneratedAtUtc = DateTime.UtcNow
        };

        using (FileStream fs = File.Create(outputPath))
        {
            using (ZipArchive archive = new(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                HashSet<string> createdDirs = new();

                foreach ((string filePath, long size) in fileSpecs)
                {
                    // Ensure parent directories exist as entries
                    EnsureDirectoryEntries(archive, filePath, createdDirs, manifest);

                    if (size < 0)
                    {
                        // Directory entry
                        continue;
                    }

                    // Generate deterministic content
                    byte[] content = GenerateDeterministicContent(filePath, size);
                    string sha256 = ComputeSha256(content);

                    // Determine compression
                    CompressionLevel level = size > 50 * 1024 * 1024
                        ? CompressionLevel.NoCompression // Large files stored for speed
                        : CompressionLevel.Fastest;

                    ZipArchiveEntry entry = archive.CreateEntry(filePath, level);
                    using (Stream entryStream = entry.Open())
                    {
                        await entryStream.WriteAsync(content);
                    }

                    manifest.Entries.Add(new ManifestEntry
                    {
                        FileName = filePath,
                        UncompressedSize = size,
                        Sha256 = sha256,
                        IsDirectory = false,
                        CompressionMethod = level == CompressionLevel.NoCompression ? "Store" : "Deflate"
                    });
                }

                // Embed manifest as __manifest__.json (no BOM)
                string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
                ZipArchiveEntry manifestEntry = archive.CreateEntry("__manifest__.json", CompressionLevel.Fastest);
                using (StreamWriter writer = new(manifestEntry.Open(),
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    await writer.WriteAsync(manifestJson);
                }
            }
        }
    }

    /// <summary>
    /// Generates the full 100-ZIP test fixture across a multi-folder structure.
    /// </summary>
    public static async Task GenerateTestFixtureAsync(string rootDir, bool smallScale = false)
    {
        Directory.CreateDirectory(rootDir);

        // Use small scale for fast tests (fewer files, smaller sizes)
        var folders = smallScale
            ? GetSmallScaleFixture()
            : GetFullScaleFixture();

        int globalSeed = 0;
        foreach ((string folder, int count, ZipProfile profile) in folders)
        {
            string folderPath = Path.Combine(rootDir, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folderPath);

            for (int i = 0; i < count; i++)
            {
                string zipName = $"archive{i + 1:D2}.zip";
                string zipPath = Path.Combine(folderPath, zipName);
                await GenerateZipAsync(zipPath, profile, seed: globalSeed++);
            }
        }
    }

    /// <summary>
    /// Generates a test fixture from a custom folder/profile specification.
    /// </summary>
    public static async Task GenerateTestFixtureAsync(
        string rootDir,
        List<(string folder, int count, ZipProfile profile)> fixture)
    {
        Directory.CreateDirectory(rootDir);

        int globalSeed = 0;
        foreach ((string folder, int count, ZipProfile profile) in fixture)
        {
            string folderPath = Path.Combine(rootDir, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folderPath);

            for (int i = 0; i < count; i++)
            {
                string zipName = $"archive{i + 1:D2}.zip";
                string zipPath = Path.Combine(folderPath, zipName);
                await GenerateZipAsync(zipPath, profile, seed: globalSeed++);
            }
        }
    }

    /// <summary>
    /// Generates deterministic pseudo-random content seeded by file path.
    /// </summary>
    public static byte[] GenerateDeterministicContent(string filePath, long size)
    {
        if (size <= 0) return [];
        if (size > int.MaxValue) size = int.MaxValue; // Cap for test purposes

        int hash = filePath.GetHashCode(StringComparison.Ordinal);
        Random rng = new(hash);

        byte[] buffer = new byte[(int)size];
        rng.NextBytes(buffer);
        return buffer;
    }

    /// <summary>
    /// Computes SHA-256 hash of content.
    /// </summary>
    public static string ComputeSha256(byte[] content)
    {
        byte[] hash = SHA256.HashData(content);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Loads and deserializes __manifest__.json from a ZIP file.
    /// </summary>
    public static async Task<ZipManifest> LoadManifestFromZipAsync(string zipPath)
    {
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
        {
            ZipArchiveEntry? entry = archive.GetEntry("__manifest__.json");
            if (entry == null)
                throw new InvalidOperationException($"No __manifest__.json found in {zipPath}");

            using (Stream stream = entry.Open())
            {
                return await JsonSerializer.DeserializeAsync<ZipManifest>(stream) ??
                       throw new InvalidOperationException("Failed to deserialize manifest");
            }
        }
    }

    /// <summary>
    /// Verifies file content against manifest using SHA-256.
    /// </summary>
    public static bool VerifyContent(byte[] content, ManifestEntry entry)
    {
        string actualSha256 = ComputeSha256(content);
        return string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // === File spec generation per profile ===

    private static List<(string path, long size)> GetFileSpecs(ZipProfile profile, int seed)
    {
        Random rng = new(42 + seed);
        return profile switch
        {
            ZipProfile.TinyFiles => GenerateTinyFiles(rng),
            ZipProfile.SmallFiles => GenerateSmallFiles(rng),
            ZipProfile.MixedFiles => GenerateMixedFiles(rng),
            ZipProfile.LargeFiles => GenerateLargeFiles(rng),
            ZipProfile.DeepNesting => GenerateDeepNesting(rng),
            ZipProfile.FlatStructure => GenerateFlatStructure(rng),
            ZipProfile.VideoSimulation => GenerateVideoSimulation(rng),
            ZipProfile.EdgeCases => GenerateEdgeCases(rng),
            ZipProfile.EnduranceMixed => GenerateEnduranceMixed(rng),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }

    private static List<(string, long)> GenerateTinyFiles(Random rng)
    {
        List<(string, long)> files = new();
        string[] dirs = ["docs", "config", "data/sub1", "data/sub2"];
        for (int i = 0; i < 50; i++)
        {
            string dir = dirs[i % dirs.Length];
            long size = rng.Next(1024, 10 * 1024); // 1KB-10KB
            files.Add(($"{dir}/file{i:D3}.dat", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateSmallFiles(Random rng)
    {
        List<(string, long)> files = new();
        string[] dirs = ["assets", "assets/images", "assets/sounds", "lib"];
        for (int i = 0; i < 100; i++)
        {
            string dir = dirs[i % dirs.Length];
            long size = rng.Next(100 * 1024, 5 * 1024 * 1024); // 100KB-5MB
            files.Add(($"{dir}/item{i:D3}.bin", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateMixedFiles(Random rng)
    {
        List<(string, long)> files = new();
        // 80 small files (1KB-10MB)
        for (int i = 0; i < 80; i++)
        {
            long size = rng.Next(1024, 10 * 1024 * 1024);
            files.Add(($"small/file{i:D3}.dat", size));
        }
        // 10 medium files (20-49MB)
        for (int i = 0; i < 10; i++)
        {
            long size = rng.Next(20 * 1024 * 1024, 49 * 1024 * 1024);
            files.Add(($"medium/file{i:D2}.bin", size));
        }
        // 10 large files (50-200MB)
        for (int i = 0; i < 10; i++)
        {
            long size = rng.Next(50 * 1024 * 1024, 200 * 1024 * 1024);
            files.Add(($"large/file{i:D2}.big", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateLargeFiles(Random rng)
    {
        List<(string, long)> files = new();
        for (int i = 0; i < 20; i++)
        {
            long size = rng.Next(50 * 1024 * 1024, 500 * 1024 * 1024);
            files.Add(($"volume{i:D2}/data.bin", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateDeepNesting(Random rng)
    {
        List<(string, long)> files = new();
        for (int i = 0; i < 200; i++)
        {
            int depth = rng.Next(5, 12);
            string path = string.Join("/", Enumerable.Range(0, depth).Select(d => $"d{d}"));
            long size = rng.Next(1024, 100 * 1024);
            files.Add(($"{path}/file{i:D3}.txt", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateFlatStructure(Random rng)
    {
        List<(string, long)> files = new();
        for (int i = 0; i < 500; i++)
        {
            long size = rng.Next(512, 50 * 1024);
            files.Add(($"file{i:D4}.dat", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateVideoSimulation(Random rng)
    {
        List<(string, long)> files = new();
        for (int i = 0; i < 5; i++)
        {
            long size = rng.Next(100 * 1024 * 1024, 500 * 1024 * 1024);
            files.Add(($"video{i + 1}.mp4", size));
        }
        return files;
    }

    private static List<(string, long)> GenerateEdgeCases(Random rng)
    {
        return new List<(string, long)>
        {
            ("empty_file.txt", 0),
            ("single_byte.bin", 1),
            ("exactly_1kb.dat", 1024),
            ("boundary_50mb.dat", 50 * 1024 * 1024), // Exact cutoff
            ("small_text.txt", 256),
            ("normal_file.bin", 100 * 1024),
            ("nested/deep/edge/file.txt", 512),
            ("special chars (1).txt", 1024),
            ("file-with-dashes.dat", 2048),
            ("UPPERCASE.TXT", 1024),
        };
    }

    private static List<(string, long)> GenerateEnduranceMixed(Random rng)
    {
        List<(string, long)> files = new();
        // 15 small files (100KB-4MB) → memory tier at 5MB cutoff
        for (int i = 0; i < 15; i++)
        {
            string dir = i % 3 == 0 ? "assets" : i % 3 == 1 ? "data" : "config";
            long size = rng.Next(100 * 1024, 4 * 1024 * 1024);
            files.Add(($"{dir}/small{i:D2}.bin", size));
        }
        // 3 medium files (5-6MB) → disk tier at 5MB cutoff
        for (int i = 0; i < 3; i++)
        {
            long size = rng.Next(5 * 1024 * 1024, 6 * 1024 * 1024);
            files.Add(($"large/medium{i:D2}.bin", size));
        }
        return files;
    }

    private static void EnsureDirectoryEntries(
        ZipArchive archive, string filePath, HashSet<string> created, ZipManifest manifest)
    {
        string[] parts = filePath.Split('/');
        string current = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = i == 0 ? parts[i] + "/" : current + parts[i] + "/";
            if (created.Add(current))
            {
                archive.CreateEntry(current);
                manifest.Entries.Add(new ManifestEntry
                {
                    FileName = current,
                    UncompressedSize = 0,
                    Sha256 = "",
                    IsDirectory = true,
                    CompressionMethod = "None"
                });
            }
        }
    }

    private static List<(string folder, int count, ZipProfile profile)> GetFullScaleFixture() =>
    [
        ("games/fps", 10, ZipProfile.MixedFiles),
        ("games/rpg", 10, ZipProfile.SmallFiles),
        ("games/retro/classic", 5, ZipProfile.TinyFiles),
        ("docs/manuals", 10, ZipProfile.SmallFiles),
        ("docs/technical/deep/nested", 5, ZipProfile.DeepNesting),
        ("media/videos", 10, ZipProfile.VideoSimulation),
        ("media/music", 10, ZipProfile.SmallFiles),
        ("archives", 15, ZipProfile.MixedFiles),
        ("backup", 10, ZipProfile.LargeFiles),
        ("", 5, ZipProfile.EdgeCases),
        ("edge", 10, ZipProfile.EdgeCases),
    ];

    private static List<(string folder, int count, ZipProfile profile)> GetSmallScaleFixture() =>
    [
        ("games/fps", 2, ZipProfile.TinyFiles),
        ("games/rpg", 2, ZipProfile.TinyFiles),
        ("docs/manuals", 2, ZipProfile.TinyFiles),
        ("", 1, ZipProfile.EdgeCases),
        ("edge", 2, ZipProfile.EdgeCases),
    ];

    /// <summary>
    /// Endurance-optimized fixture: small + medium files for dual-tier cache testing.
    /// Total ~50MB. Designed for SmallFileCutoffMb=5.
    /// </summary>
    public static List<(string folder, int count, ZipProfile profile)> GetEnduranceFixture() =>
    [
        ("small", 3, ZipProfile.TinyFiles),          // Many small files → memory tier
        ("mixed", 2, ZipProfile.EnduranceMixed),      // Files spanning both tiers
        ("edge", 1, ZipProfile.EdgeCases),            // Edge cases (0-byte, 1-byte, boundary)
    ];
}
