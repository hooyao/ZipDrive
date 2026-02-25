using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Static telemetry definitions for the ZIP reader subsystem.
/// Uses System.Diagnostics.Metrics and System.Diagnostics.ActivitySource (no OTel dependency).
/// </summary>
public static class ZipTelemetry
{
    internal const string MeterName = "ZipDrive.Zip";
    internal const string ActivitySourceName = "ZipDrive.Zip";

    internal static readonly Meter Meter = new(MeterName);
    internal static readonly ActivitySource Source = new(ActivitySourceName);

    // === Histograms ===

    internal static readonly Histogram<double> ExtractionDuration =
        Meter.CreateHistogram<double>("zip.extraction.duration", unit: "ms",
            description: "Time to extract a file from a ZIP archive");

    // === Counters ===

    internal static readonly Counter<long> BytesExtracted =
        Meter.CreateCounter<long>("zip.bytes_extracted", unit: "By",
            description: "Total bytes extracted from ZIP archives");

    public static readonly Counter<long> EncodingDetections =
        Meter.CreateCounter<long>("zip.encoding.detections",
            description: "Encoding detection outcomes for ZIP filenames");
}
