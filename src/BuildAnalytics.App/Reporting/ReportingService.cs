using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.App.Reporting;

/// <summary>Report produced from the local run log plus how it was assembled.</summary>
public sealed record ReportingResult(TimingReport Report, int RunsRead, int CorruptSkipped);

/// <summary>
/// Offline reporting (ADR-74..77, ADR-110): reads every run from the append-only log,
/// aggregates in Core, and writes via <see cref="ITimingReportWriter"/>. No manifest is
/// required and an empty log produces a valid zero-filled report.
/// </summary>
public sealed class ReportingService(
    IRunStore runs,
    ITimingReportWriter writer)
{
    public async Task<ReportingResult> GenerateAsync(CancellationToken cancellationToken)
    {
        var read = await runs.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var loaded = read.Runs;

        var summary = MonthlyTimingRollup.Summarize(loaded);
        var report = new TimingReport(summary, loaded);
        await writer.WriteAsync(report, cancellationToken).ConfigureAwait(false);

        return new ReportingResult(report, loaded.Count, read.MalformedLineCount);
    }
}
