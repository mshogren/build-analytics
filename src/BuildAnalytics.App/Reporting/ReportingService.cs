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

        var runIds = await runs.ListRunIdsAsync(cancellationToken).ConfigureAwait(false);
        var loaded = new List<BuildRun>();
        var corruptSkipped = 0;

        foreach (var runId in runIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BuildRun? run;
            try
            {
                run = await runs.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            catch (CorruptRunFileException)
            {
                // ADR-77: a corrupt run file is skipped and counted.
                corruptSkipped++;
                continue;
            }

            // ADR-77: a listed id whose file is absent is skipped silently. An unsupported
            // run schemaVersion is not caught here and aborts (ADR-77/83 asymmetry).
            if (run is not null)
            {
                loaded.Add(run);
            }
        }

        var summary = MonthlyTimingRollup.Summarize(loaded);
        var report = new TimingReport(summary, loaded);
        await writer.WriteAsync(report, cancellationToken).ConfigureAwait(false);

        return new ReportingResult(report, loaded.Count, corruptSkipped);
    }
}
