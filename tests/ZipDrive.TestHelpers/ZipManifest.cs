using System.Text.Json.Serialization;

namespace ZipDrive.TestHelpers;

/// <summary>
/// Manifest embedded in test ZIPs at __manifest__.json.
/// Contains metadata for each file to enable content verification.
/// </summary>
public sealed class ZipManifest
{
    [JsonPropertyName("entries")]
    public List<ManifestEntry> Entries { get; set; } = new();

    [JsonPropertyName("partialSamples")]
    public List<PartialSample> PartialSamples { get; set; } = new();

    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAtUtc { get; set; }
}

/// <summary>
/// Metadata for a single file in a test ZIP.
/// </summary>
public sealed class ManifestEntry
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("uncompressedSize")]
    public long UncompressedSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("compressionMethod")]
    public string CompressionMethod { get; set; } = "Deflate";
}

/// <summary>
/// Pre-computed SHA-256 checksum for a byte range within a file.
/// Used by endurance tests to verify partial/random-offset reads.
/// </summary>
public sealed class PartialSample
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("offset")]
    public long Offset { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}
