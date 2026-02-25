using System.Text;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Result of charset detection for ZIP filename bytes.
/// </summary>
/// <param name="Encoding">The detected encoding.</param>
/// <param name="Confidence">Detection confidence (0.0 to 1.0).</param>
public record DetectionResult(Encoding Encoding, float Confidence);
