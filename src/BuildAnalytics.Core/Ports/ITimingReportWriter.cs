using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Core.Ports;

/// <summary>Emits a timing summary. Excel is the v1 adapter; tests use an in-memory adapter.</summary>
public interface ITimingReportWriter
{
    Task WriteAsync(TimingSummary summary, CancellationToken cancellationToken);
}
