using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Timing;

/// <summary>
/// A timing summary together with the raw runs it was built from (ADR-105), so the report
/// adapter can render both the aggregate sheets and a raw <c>Runs</c> sheet.
/// </summary>
public sealed record TimingReport(TimingSummary Summary, IReadOnlyList<BuildRun> Runs);
