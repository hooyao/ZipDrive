using System.Diagnostics.Metrics;

namespace ZipDrive.Application.Services;

/// <summary>
/// Static telemetry definitions for the prefetch subsystem.
/// Emits metrics into the "ZipDrive.Caching" meter so they appear
/// alongside the rest of the caching metrics in OTel dashboards.
/// </summary>
internal static class PrefetchTelemetry
{
    private static readonly Meter Meter = new("ZipDrive.Caching");

    internal static readonly Counter<long> FilesWarmed =
        Meter.CreateCounter<long>("prefetch.files_warmed",
            unit: "{file}", description: "Number of sibling files warmed by prefetch");

    internal static readonly Counter<long> BytesRead =
        Meter.CreateCounter<long>("prefetch.bytes_read",
            unit: "By", description: "Total bytes read during prefetch spans");

    internal static readonly Counter<long> SkippedInFlight =
        Meter.CreateCounter<long>("prefetch.skipped_inflight",
            unit: "{skip}", description: "Number of prefetch attempts skipped because a scan was already in-flight");

    internal static readonly Histogram<double> SpanReadDuration =
        Meter.CreateHistogram<double>("prefetch.span_read_duration",
            unit: "ms", description: "Duration of a sequential span read during prefetch");
}
