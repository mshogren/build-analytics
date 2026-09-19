using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.App.Reporting;

/// <summary>Report produced from local raw files plus how it was assembled.</summary>
public sealed record ReportingResult(TimingReport Report, int RunsRead, int CorruptSkipped);

/// <summary>
/// Offline reporting (ADR-74..77): reads a completed root through the read-only manifest
/// store and run store, aggregates in Core, and writes via <see cref="ITimingReportWriter"/>.
/// No network and no writes to the retrieval artifacts.
/// </summary>
public sealed class ReportingService(
    IManifestStore manifestReadOnly,
    IRunStore runs,
    ITimingReportWriter writer)
{
    public async Task<ReportingResult> GenerateAsync(CancellationToken cancellationToken)
    {
        var manifest = await manifestReadOnly.TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Status != ManifestStatus.Completed)
        {
            throw new ReportingErrorException(manifest?.Status);
        }

        var read = await runs.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        // ADR-77/109: an unsupported run schema aborts reporting (it cannot repair offline).
        if (read.UnsupportedSchemaLineCount > 0)
        {
            throw new UnsupportedSchemaVersionException(BuildRun.CurrentSchemaVersion);
        }

        var loaded = read.Runs;
        var summary = MonthlyTimingRollup.Summarize(loaded);
        var report = new TimingReport(summary, loaded);
        await writer.WriteAsync(report, cancellationToken).ConfigureAwait(false);

        // ADR-109: malformed lines are skipped and surfaced as the corrupt count.
        return new ReportingResult(report, loaded.Count, read.MalformedLineCount);
    }
}
