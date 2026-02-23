using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZipDriveV3.Infrastructure.FileSystem;

/// <summary>
/// Static telemetry definitions for the Dokan file system adapter.
/// Uses System.Diagnostics.Metrics (no OTel dependency).
/// </summary>
internal static class DokanTelemetry
{
    internal const string MeterName = "ZipDriveV3.Dokan";

    internal static readonly Meter Meter = new(MeterName);

    // === Histograms ===

    internal static readonly Histogram<double> ReadDuration =
        Meter.CreateHistogram<double>("dokan.read.duration", unit: "ms",
            description: "Time to process a Dokan ReadFile request");
}
