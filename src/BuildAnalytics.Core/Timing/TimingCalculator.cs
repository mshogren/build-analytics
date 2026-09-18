using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Timing;

/// <summary>
/// Pure per-run duration computation. Stores raw seconds; rounding is a reporting concern.
/// </summary>
public static class TimingCalculator
{
    /// <summary>Runs waiting strictly more than this many raw seconds count as "wait over 5 min".</summary>
    public const double WaitOverFiveMinutesThresholdSeconds = 300d;

    public static RunTiming Calculate(BuildRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new RunTiming(
            Elapsed(run.QueueTime, run.StartTime),
            Elapsed(run.StartTime, run.FinishTime),
            Elapsed(run.QueueTime, run.FinishTime));
    }

    private static double? Elapsed(DateTimeOffset? start, DateTimeOffset? end)
        => start is { } from && end is { } to && to >= from ? (to - from).TotalSeconds : null;
}
