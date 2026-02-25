using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using UtfUnknown;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Detects filename encoding using a three-tier chain:
/// 1. UtfUnknown statistical detection
/// 2. System OEM code page validation
/// 3. Returns null (caller uses configured fallback)
/// </summary>
public sealed class FilenameEncodingDetector : IFilenameEncodingDetector
{
    private readonly float _confidenceThreshold;
    private readonly ILogger<FilenameEncodingDetector>? _logger;

    public FilenameEncodingDetector(float confidenceThreshold = 0.5f, ILogger<FilenameEncodingDetector>? logger = null)
    {
        _confidenceThreshold = confidenceThreshold;
        _logger = logger;
    }

    /// <inheritdoc />
    public DetectionResult? DetectEncoding(IReadOnlyList<byte[]> filenameByteArrays)
    {
        if (filenameByteArrays.Count == 0)
            return null;

        // Concatenate all filename bytes with NUL separator for statistical accuracy
        int totalLength = 0;
        for (int i = 0; i < filenameByteArrays.Count; i++)
        {
            totalLength += filenameByteArrays[i].Length;
            if (i < filenameByteArrays.Count - 1)
                totalLength++; // NUL separator
        }

        byte[] concatenated = new byte[totalLength];
        int offset = 0;
        for (int i = 0; i < filenameByteArrays.Count; i++)
        {
            filenameByteArrays[i].CopyTo(concatenated, offset);
            offset += filenameByteArrays[i].Length;
            if (i < filenameByteArrays.Count - 1)
            {
                concatenated[offset] = 0; // NUL separator
                offset++;
            }
        }

        return DetectFromBytes(concatenated);
    }

    /// <inheritdoc />
    public DetectionResult? DetectSingleEntry(byte[] filenameBytes)
    {
        if (filenameBytes.Length == 0)
            return null;

        return DetectFromBytes(filenameBytes);
    }

    private DetectionResult? DetectFromBytes(byte[] bytes)
    {
        // Tier 1: UtfUnknown statistical detection
        DetectionResult? utfResult = TryUtfUnknown(bytes);
        if (utfResult != null)
            return utfResult;

        // Tier 2: System OEM code page
        DetectionResult? oemResult = TrySystemOem(bytes);
        if (oemResult != null)
            return oemResult;

        // Tier 3: Return null — caller uses configured fallback
        _logger?.LogDebug("No encoding detected with sufficient confidence");
        return null;
    }

    private DetectionResult? TryUtfUnknown(byte[] bytes)
    {
        try
        {
            DetectionDetail? best = CharsetDetector.DetectFromBytes(bytes).Detected;

            if (best?.EncodingName == null)
                return null;

            float confidence = best.Confidence;
            if (confidence < _confidenceThreshold)
            {
                _logger?.LogDebug(
                    "UtfUnknown detected {Encoding} with confidence {Confidence} (below threshold {Threshold})",
                    best.EncodingName, confidence, _confidenceThreshold);
                return null;
            }

            Encoding encoding = Encoding.GetEncoding(best.EncodingName);

            _logger?.LogDebug(
                "UtfUnknown detected {Encoding} with confidence {Confidence}",
                encoding.WebName, confidence);

            return new DetectionResult(encoding, confidence);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "UtfUnknown returned unsupported encoding");
            return null;
        }
    }

    private DetectionResult? TrySystemOem(byte[] bytes)
    {
        try
        {
            int oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

            // Skip if OEM is CP437 (that's the existing default, no improvement)
            if (oemCodePage == 437)
                return null;

            Encoding oemEncoding = Encoding.GetEncoding(oemCodePage);
            string decoded = oemEncoding.GetString(bytes);

            // Reject if decoding produces replacement characters
            if (decoded.Contains('\uFFFD'))
            {
                _logger?.LogDebug(
                    "System OEM code page {CodePage} produced replacement characters, rejecting",
                    oemCodePage);
                return null;
            }

            _logger?.LogDebug("System OEM code page {CodePage} ({Name}) accepted", oemCodePage, oemEncoding.WebName);
            return new DetectionResult(oemEncoding, 0.3f); // Low confidence for OEM guess
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Failed to use system OEM code page");
            return null;
        }
    }
}
