using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UtfUnknown;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Detects filename encoding using a three-tier chain:
/// 1. UtfUnknown statistical detection
/// 2. System OEM code page validation
/// 3. Configured fallback encoding
/// </summary>
public sealed class FilenameEncodingDetector : IFilenameEncodingDetector
{
    private readonly float _confidenceThreshold;
    private readonly Encoding _fallbackEncoding;
    private readonly ILogger<FilenameEncodingDetector> _logger;

    public FilenameEncodingDetector(
        IOptions<EncodingDetectionOptions> options,
        ILogger<FilenameEncodingDetector> logger)
    {
        var opts = options.Value;
        _confidenceThreshold = opts.EncodingConfidenceThreshold;
        _logger = logger;

        try
        {
            _fallbackEncoding = Encoding.GetEncoding(opts.FallbackEncoding);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning(
                "Invalid FallbackEncoding '{Encoding}', defaulting to UTF-8",
                opts.FallbackEncoding);
            _fallbackEncoding = Encoding.UTF8;
        }

        _logger.LogInformation(
            "FilenameEncodingDetector initialized: fallback={Fallback}, threshold={Threshold}",
            _fallbackEncoding.WebName, _confidenceThreshold);
    }

    /// <inheritdoc />
    public Encoding? DetectArchiveEncoding(IReadOnlyList<byte[]> filenameByteArrays)
    {
        if (filenameByteArrays.Count == 0)
            return null;

        // Concatenate all filename bytes with NUL separator for statistical accuracy
        byte[] concatenated = ConcatenateBytes(filenameByteArrays);

        Encoding? detected = TryDetectFromBytes(concatenated);
        if (detected != null)
        {
            _logger?.LogDebug(
                "Archive-level encoding detected: {Encoding}",
                detected.WebName);

            ZipTelemetry.EncodingDetections.Add(1,
                new KeyValuePair<string, object?>("result", "archive_detected"),
                new KeyValuePair<string, object?>("encoding", detected.WebName));
        }

        return detected;
    }

    /// <inheritdoc />
    public Encoding ResolveEntryEncoding(byte[] filenameBytes)
    {
        if (filenameBytes.Length == 0)
        {
            ZipTelemetry.EncodingDetections.Add(1,
                new KeyValuePair<string, object?>("result", "fallback"),
                new KeyValuePair<string, object?>("encoding", _fallbackEncoding.WebName));
            return _fallbackEncoding;
        }

        Encoding? detected = TryDetectFromBytes(filenameBytes);
        if (detected != null)
        {
            ZipTelemetry.EncodingDetections.Add(1,
                new KeyValuePair<string, object?>("result", "entry_detected"),
                new KeyValuePair<string, object?>("encoding", detected.WebName));
            return detected;
        }

        _logger?.LogDebug(
            "Per-entry detection failed, using fallback encoding {Encoding}",
            _fallbackEncoding.WebName);

        ZipTelemetry.EncodingDetections.Add(1,
            new KeyValuePair<string, object?>("result", "fallback"),
            new KeyValuePair<string, object?>("encoding", _fallbackEncoding.WebName));
        return _fallbackEncoding;
    }

    private static byte[] ConcatenateBytes(IReadOnlyList<byte[]> arrays)
    {
        int totalLength = 0;
        for (int i = 0; i < arrays.Count; i++)
        {
            totalLength += arrays[i].Length;
            if (i < arrays.Count - 1)
                totalLength++; // NUL separator
        }

        byte[] concatenated = new byte[totalLength];
        int offset = 0;
        for (int i = 0; i < arrays.Count; i++)
        {
            arrays[i].CopyTo(concatenated, offset);
            offset += arrays[i].Length;
            if (i < arrays.Count - 1)
            {
                concatenated[offset] = 0;
                offset++;
            }
        }

        return concatenated;
    }

    private Encoding? TryDetectFromBytes(byte[] bytes)
    {
        // Tier 1: UtfUnknown statistical detection
        Encoding? utfResult = TryUtfUnknown(bytes);
        if (utfResult != null)
            return utfResult;

        // Tier 2: System OEM code page
        Encoding? oemResult = TrySystemOem(bytes);
        if (oemResult != null)
            return oemResult;

        return null;
    }

    private Encoding? TryUtfUnknown(byte[] bytes)
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

            return encoding;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "UtfUnknown returned unsupported encoding");
            return null;
        }
    }

    private Encoding? TrySystemOem(byte[] bytes)
    {
        try
        {
            int oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

            // Skip if OEM is CP437 (that's the existing default, no improvement)
            if (oemCodePage == 437)
                return null;

            Encoding oemEncoding = Encoding.GetEncoding(oemCodePage);

            // Use strict decoder to reject invalid byte sequences
            Encoding strictOem = Encoding.GetEncoding(
                oemCodePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ExceptionFallback);

            try
            {
                strictOem.GetString(bytes); // Throws on invalid sequences
            }
            catch (DecoderFallbackException)
            {
                _logger?.LogDebug(
                    "System OEM code page {CodePage} produced invalid sequences, rejecting",
                    oemCodePage);
                return null;
            }

            _logger?.LogDebug("System OEM code page {CodePage} ({Name}) accepted", oemCodePage, oemEncoding.WebName);

            ZipTelemetry.EncodingDetections.Add(1,
                new KeyValuePair<string, object?>("result", "system_oem"),
                new KeyValuePair<string, object?>("encoding", oemEncoding.WebName));

            return oemEncoding;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Failed to use system OEM code page");
            return null;
        }
    }
}
