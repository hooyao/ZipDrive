using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UtfUnknown;
using ZipDrive.Domain.Configuration;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Detects filename encoding using a three-tier chain:
/// 1. UtfUnknown statistical detection
/// 2. System OEM code page validation
/// 3. Configured fallback encoding
/// </summary>
public sealed class FilenameEncodingDetector : IFilenameEncodingDetector
{
    private static readonly KeyValuePair<string, object?> TagResultArchiveDetected = new("result", "archive_detected");
    private static readonly KeyValuePair<string, object?> TagResultEntryDetected = new("result", "entry_detected");
    private static readonly KeyValuePair<string, object?> TagResultFallback = new("result", "fallback");
    private static readonly KeyValuePair<string, object?> TagResultSystemOem = new("result", "system_oem");

    private readonly float _confidenceThreshold;
    private readonly Encoding _fallbackEncoding;
    private readonly Encoding? _oemEncoding;
    private readonly Encoding? _strictOemEncoding;
    private readonly ILogger<FilenameEncodingDetector> _logger;

    public FilenameEncodingDetector(
        IOptions<MountSettings> mountSettings,
        ILogger<FilenameEncodingDetector> logger)
    {
        var settings = mountSettings.Value;
        _confidenceThreshold = Math.Clamp(settings.EncodingConfidenceThreshold, 0.0f, 1.0f);
        _logger = logger;

        try
        {
            _fallbackEncoding = Encoding.GetEncoding(settings.FallbackEncoding);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning(
                "Invalid FallbackEncoding '{Encoding}', defaulting to UTF-8",
                settings.FallbackEncoding);
            _fallbackEncoding = Encoding.UTF8;
        }

        // Pre-resolve OEM encodings once at construction (avoid per-call Encoding.GetEncoding)
        int oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        if (oemCodePage != 437)
        {
            try
            {
                _oemEncoding = Encoding.GetEncoding(oemCodePage);
                _strictOemEncoding = Encoding.GetEncoding(
                    oemCodePage,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                _logger.LogWarning(ex, "System OEM code page {CodePage} is not supported", oemCodePage);
                _oemEncoding = null;
                _strictOemEncoding = null;
            }
        }

        _logger.LogInformation(
            "FilenameEncodingDetector initialized: fallback={Fallback}, threshold={Threshold}, oem={Oem}",
            _fallbackEncoding.WebName, _confidenceThreshold, _oemEncoding?.WebName ?? "none");
    }

    /// <inheritdoc />
    public Encoding? DetectArchiveEncoding(IReadOnlyList<byte[]> filenameByteArrays)
    {
        if (filenameByteArrays.Count == 0)
            return null;

        // Calculate total length for concatenation
        int totalLength = 0;
        for (int i = 0; i < filenameByteArrays.Count; i++)
        {
            totalLength += filenameByteArrays[i].Length;
            if (i < filenameByteArrays.Count - 1)
                totalLength++; // NUL separator
        }

        // Rent pooled buffer to avoid GC pressure for large archives
        byte[] rented = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            int offset = 0;
            for (int i = 0; i < filenameByteArrays.Count; i++)
            {
                filenameByteArrays[i].CopyTo(rented, offset);
                offset += filenameByteArrays[i].Length;
                if (i < filenameByteArrays.Count - 1)
                {
                    rented[offset] = 0; // NUL separator
                    offset++;
                }
            }

            // Use offset/length overload so extra rented bytes are ignored
            Encoding? detected = TryDetectFromBytes(rented, totalLength);
            if (detected != null)
            {
                _logger.LogDebug(
                    "Archive-level encoding detected: {Encoding}",
                    detected.WebName);

                ZipTelemetry.EncodingDetections.Add(1,
                    TagResultArchiveDetected,
                    new KeyValuePair<string, object?>("encoding", detected.WebName));
            }

            return detected;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public Encoding ResolveEntryEncoding(byte[] filenameBytes)
    {
        if (filenameBytes.Length == 0)
        {
            ZipTelemetry.EncodingDetections.Add(1,
                TagResultFallback,
                new KeyValuePair<string, object?>("encoding", _fallbackEncoding.WebName));
            return _fallbackEncoding;
        }

        Encoding? detected = TryDetectFromBytes(filenameBytes, filenameBytes.Length);
        if (detected != null)
        {
            ZipTelemetry.EncodingDetections.Add(1,
                TagResultEntryDetected,
                new KeyValuePair<string, object?>("encoding", detected.WebName));
            return detected;
        }

        _logger.LogDebug(
            "Per-entry detection failed, using fallback encoding {Encoding}",
            _fallbackEncoding.WebName);

        ZipTelemetry.EncodingDetections.Add(1,
            TagResultFallback,
            new KeyValuePair<string, object?>("encoding", _fallbackEncoding.WebName));
        return _fallbackEncoding;
    }

    private Encoding? TryDetectFromBytes(byte[] bytes, int length)
    {
        // Tier 1: UtfUnknown statistical detection
        Encoding? utfResult = TryUtfUnknown(bytes, length);
        if (utfResult != null)
            return utfResult;

        // Tier 2: System OEM code page (pre-resolved at construction)
        Encoding? oemResult = TrySystemOem(bytes.AsSpan(0, length));
        if (oemResult != null)
            return oemResult;

        return null;
    }

    private Encoding? TryUtfUnknown(byte[] bytes, int length)
    {
        try
        {
            // Use offset/length overload — works with pooled arrays that may be oversized
            DetectionDetail? best = CharsetDetector.DetectFromBytes(bytes, 0, length).Detected;

            if (best?.EncodingName == null)
                return null;

            float confidence = best.Confidence;
            if (confidence < _confidenceThreshold)
            {
                _logger.LogDebug(
                    "UtfUnknown detected {Encoding} with confidence {Confidence} (below threshold {Threshold})",
                    best.EncodingName, confidence, _confidenceThreshold);
                return null;
            }

            Encoding encoding = Encoding.GetEncoding(best.EncodingName);

            _logger.LogDebug(
                "UtfUnknown detected {Encoding} with confidence {Confidence}",
                encoding.WebName, confidence);

            return encoding;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "UtfUnknown returned unsupported encoding");
            return null;
        }
    }

    private Encoding? TrySystemOem(ReadOnlySpan<byte> bytes)
    {
        if (_strictOemEncoding == null || _oemEncoding == null)
            return null;

        try
        {
            // GetCharCount validates byte sequences without allocating a string.
            // With ExceptionFallback, it throws DecoderFallbackException on invalid sequences.
            _strictOemEncoding.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            _logger.LogDebug(
                "System OEM code page {CodePage} produced invalid sequences, rejecting",
                _oemEncoding.CodePage);
            return null;
        }

        _logger.LogDebug("System OEM code page {CodePage} ({Name}) accepted",
            _oemEncoding.CodePage, _oemEncoding.WebName);

        ZipTelemetry.EncodingDetections.Add(1,
            TagResultSystemOem,
            new KeyValuePair<string, object?>("encoding", _oemEncoding.WebName));

        return _oemEncoding;
    }
}
