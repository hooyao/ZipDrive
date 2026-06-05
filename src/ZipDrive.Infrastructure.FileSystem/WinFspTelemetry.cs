using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Static telemetry definitions for the WinFsp file system adapter.
/// Uses System.Diagnostics.Metrics (no OTel dependency).
/// </summary>
internal static class WinFspTelemetry
{
    internal const string MeterName = "ZipDrive.WinFsp";

    internal static readonly Meter Meter = new(MeterName);

    // === Histograms ===

    internal static readonly Histogram<double> ReadDuration =
        Meter.CreateHistogram<double>("winfsp.read.duration", unit: "ms",
            description: "Time to process a WinFsp ReadFile request");
}
