namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Configuration options for automatic ZIP filename encoding detection.
/// Bound from the "Mount" section of appsettings.json.
/// </summary>
public class EncodingDetectionOptions
{
    /// <summary>
    /// Fallback encoding name for non-UTF8 ZIP filenames when automatic detection fails.
    /// Accepts any .NET encoding name (e.g., "utf-8", "shift_jis", "gb2312").
    /// Default is "utf-8".
    /// </summary>
    public string FallbackEncoding { get; set; } = "utf-8";

    /// <summary>
    /// Minimum confidence threshold (0.0 to 1.0) for automatic charset detection.
    /// Detection results below this threshold are rejected. Default is 0.5.
    /// </summary>
    public float EncodingConfidenceThreshold { get; set; } = 0.5f;
}
