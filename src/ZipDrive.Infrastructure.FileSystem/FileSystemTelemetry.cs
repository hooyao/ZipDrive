using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Static telemetry definitions for the file system adapter.
/// Uses System.Diagnostics.Metrics (no OTel dependency).
/// </summary>
internal static class FileSystemTelemetry
{
    internal const string MeterName = "ZipDrive.FileSystem";

    internal static readonly Meter Meter = new(MeterName);

    // === Histograms ===

    internal static readonly Histogram<double> ReadDuration =
        Meter.CreateHistogram<double>("fs.read.duration", unit: "ms",
            description: "Time to process a ReadFile request");
}
