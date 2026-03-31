using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZipDrive.TestHelpers;

/// <summary>
/// Generates test RAR files with embedded manifests for endurance testing.
/// Supports randomized fixture generation with configurable distributions.
/// </summary>
public static class TestRarGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Describes the desired archive characteristics. The generator picks random
    /// file counts and sizes within the specified ranges for each archive.
    /// </summary>
    public sealed class ArchiveSpec
    {
        /// <summary>Human-readable label for logging and manifest.</summary>
        public required string Label { get; init; }

        /// <summary>Min number of files to generate in the archive.</summary>
        public int MinFiles { get; init; } = 5;

        /// <summary>Max number of files to generate in the archive.</summary>
        public int MaxFiles { get; init; } = 30;

        /// <summary>Min size of each file in bytes.</summary>
        public long MinFileSize { get; init; } = 512;

        /// <summary>Max size of each file in bytes.</summary>
        public long MaxFileSize { get; init; } = 256 * 1024;

        /// <summary>Max total archive content size in bytes (caps file generation).</summary>
        public long MaxTotalBytes { get; init; } = 50 * 1024 * 1024;

        /// <summary>Max directory nesting depth for generated paths.</summary>
        public int MaxDepth { get; init; } = 3;

        /// <summary>Include some empty (0-byte) files.</summary>
        public bool IncludeEmptyFiles { get; init; } = false;
    }

    /// <summary>All small files — memory tier stress.</summary>
    public static ArchiveSpec SmallFilesSpec => new()
    {
        Label = "SmallFiles",
        MinFiles = 15, MaxFiles = 40,
        MinFileSize = 256, MaxFileSize = 500 * 1024,    // 256B - 500KB
        MaxTotalBytes = 10 * 1024 * 1024,                // ~10MB total
        MaxDepth = 3,
    };

    /// <summary>All large files — disk tier stress.</summary>
    public static ArchiveSpec LargeFilesSpec => new()
    {
        Label = "LargeFiles",
        MinFiles = 3, MaxFiles = 8,
        MinFileSize = 1 * 1024 * 1024, MaxFileSize = 8 * 1024 * 1024,  // 1MB - 8MB
        MaxTotalBytes = 30 * 1024 * 1024,
        MaxDepth = 2,
    };

    /// <summary>Mixed sizes spanning both cache tiers.</summary>
    public static ArchiveSpec MixedSpec => new()
    {
        Label = "Mixed",
        MinFiles = 10, MaxFiles = 25,
        MinFileSize = 512, MaxFileSize = 4 * 1024 * 1024,  // 512B - 4MB
        MaxTotalBytes = 20 * 1024 * 1024,
        MaxDepth = 4,
    };

    /// <summary>Deep nested paths — path resolution stress.</summary>
    public static ArchiveSpec DeepNestedSpec => new()
    {
        Label = "DeepNested",
        MinFiles = 8, MaxFiles = 15,
        MinFileSize = 256, MaxFileSize = 64 * 1024,
        MaxTotalBytes = 5 * 1024 * 1024,
        MaxDepth = 8,
    };

    /// <summary>Edge cases — empty files, boundary sizes, exotic names.</summary>
    public static ArchiveSpec EdgeCasesSpec => new()
    {
        Label = "EdgeCases",
        MinFiles = 6, MaxFiles = 10,
        MinFileSize = 0, MaxFileSize = 2 * 1024 * 1024,
        MaxTotalBytes = 5 * 1024 * 1024,
        MaxDepth = 5,
        IncludeEmptyFiles = true,
    };

    /// <summary>
    /// Returns the endurance fixture specs: enough variety to stress both tiers,
    /// directory resolution, and concurrent access patterns.
    /// </summary>
    public static List<(string folder, int count, ArchiveSpec spec)> GetEnduranceFixture() =>
    [
        ("rar/small", 2, SmallFilesSpec),     // 2 archives × small files → memory tier
        ("rar/large", 1, LargeFilesSpec),     // 1 archive × large files → disk tier
        ("rar/mixed", 2, MixedSpec),          // 2 archives × mixed sizes
        ("rar/deep", 1, DeepNestedSpec),      // 1 archive × deep paths
        ("rar/edge", 1, EdgeCasesSpec),       // 1 archive × edge cases
    ];

    /// <summary>
    /// Generates the full RAR endurance fixture set with randomized content.
    /// Each run produces different file counts and sizes within the spec ranges.
    /// </summary>
    public static async Task GenerateEnduranceFixtureAsync(string rootDir, string rarExePath, int? seed = null)
    {
        Random masterRng = seed.HasValue ? new Random(seed.Value) : Random.Shared;

        foreach ((string folder, int count, ArchiveSpec spec) in GetEnduranceFixture())
        {
            string folderPath = Path.Combine(rootDir, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(folderPath);

            for (int i = 0; i < count; i++)
            {
                string rarPath = Path.Combine(folderPath, $"archive{i + 1:D2}.rar");
                int archiveSeed = masterRng.Next();
                await GenerateRarFromSpecAsync(rarPath, rarExePath, spec, archiveSeed);
            }
        }
    }

    /// <summary>
    /// Generates a single RAR archive from a spec with randomized files.
    /// </summary>
    public static async Task GenerateRarFromSpecAsync(
        string outputPath, string rarExePath, ArchiveSpec spec, int seed)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"rar_gen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            Random rng = new(seed);
            int fileCount = rng.Next(spec.MinFiles, spec.MaxFiles + 1);
            long totalBytes = 0;

            var manifest = new ZipManifest
            {
                Profile = $"{spec.Label}_seed{seed}",
                GeneratedAtUtc = DateTime.UtcNow
            };

            // Pre-generate directory pool for randomized paths
            string[] dirNames = ["assets", "data", "src", "lib", "docs", "test", "config", "util", "core", "io"];

            for (int i = 0; i < fileCount; i++)
            {
                // Random file size within spec range
                long fileSize;
                if (spec.IncludeEmptyFiles && i == 0)
                    fileSize = 0; // Guarantee at least one empty file
                else if (spec.MinFileSize == spec.MaxFileSize)
                    fileSize = spec.MinFileSize;
                else
                    fileSize = spec.MinFileSize + (long)(rng.NextDouble() * (spec.MaxFileSize - spec.MinFileSize));

                // Cap at remaining budget
                if (totalBytes + fileSize > spec.MaxTotalBytes)
                    fileSize = Math.Max(0, spec.MaxTotalBytes - totalBytes);

                if (fileSize <= 0 && !spec.IncludeEmptyFiles && totalBytes > 0)
                    break; // Budget exhausted

                // Random directory path
                int depth = rng.Next(1, spec.MaxDepth + 1);
                StringBuilder pathBuilder = new();
                for (int d = 0; d < depth; d++)
                {
                    pathBuilder.Append(dirNames[rng.Next(dirNames.Length)]);
                    pathBuilder.Append('/');
                }

                string ext = (rng.Next(4)) switch
                {
                    0 => ".dat",
                    1 => ".bin",
                    2 => ".txt",
                    _ => ".raw"
                };
                string fileName = $"f{i:D3}_{fileSize}{ext}";
                string relativePath = pathBuilder.ToString() + fileName;

                string fullPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                // Generate content — mix of random and patterned data
                byte[] content;
                if (fileSize == 0)
                {
                    content = [];
                }
                else
                {
                    content = new byte[(int)Math.Min(fileSize, int.MaxValue)];
                    if (rng.Next(3) == 0)
                    {
                        // 1/3 chance: highly compressible (repeated pattern)
                        byte pattern = (byte)rng.Next(256);
                        Array.Fill(content, pattern);
                        // Add some variation to avoid trivial dedup
                        for (int j = 0; j < content.Length; j += 1024)
                            content[j] = (byte)(j & 0xFF);
                    }
                    else
                    {
                        // 2/3 chance: pseudo-random (incompressible)
                        rng.NextBytes(content);
                    }
                }

                await File.WriteAllBytesAsync(fullPath, content);
                totalBytes += fileSize;

                manifest.Entries.Add(new ManifestEntry
                {
                    FileName = relativePath,
                    UncompressedSize = fileSize,
                    Sha256 = TestZipGenerator.ComputeSha256(content),
                    IsDirectory = false,
                    CompressionMethod = "RAR"
                });

                if (fileSize > 0)
                {
                    manifest.PartialSamples.AddRange(
                        TestZipGenerator.GeneratePartialSamples(relativePath, content, chunkSize: 1024 * 1024));
                }
            }

            // Write manifest inside the archive
            string manifestPath = Path.Combine(tempDir, "__manifest__.json");
            string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            await File.WriteAllTextAsync(manifestPath, manifestJson, new UTF8Encoding(false));

            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
            manifest.Entries.Add(new ManifestEntry
            {
                FileName = "__manifest__.json",
                UncompressedSize = manifestBytes.Length,
                Sha256 = TestZipGenerator.ComputeSha256(manifestBytes),
                IsDirectory = false,
                CompressionMethod = "RAR"
            });

            // Run rar.exe: non-solid RAR5, recurse directories
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string absOutput = Path.GetFullPath(outputPath);

            var psi = new ProcessStartInfo
            {
                FileName = rarExePath,
                Arguments = $"a -r -s- -ma5 \"{absOutput}\" *",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();

            if (!File.Exists(absOutput))
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"rar.exe failed (exit={process.ExitCode}): {stderr}");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>Backward-compatible overload.</summary>
    public static Task GenerateRarAsync(string outputPath, string rarExePath,
        int fileCount = 5, int seed = 0)
    {
        return GenerateRarFromSpecAsync(outputPath, rarExePath, new ArchiveSpec
        {
            Label = "Legacy",
            MinFiles = fileCount, MaxFiles = fileCount,
            MinFileSize = 512, MaxFileSize = 65536,
            MaxTotalBytes = 10 * 1024 * 1024,
            MaxDepth = 1,
        }, seed);
    }

    /// <summary>Finds rar.exe.</summary>
    public static string? FindRarExe()
    {
        string? envPath = Environment.GetEnvironmentVariable("RAR_EXE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Rar.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "ZipDrive.Infrastructure.Archives.Rar.Tests", "Rar.exe"),
        ];

        foreach (string candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
