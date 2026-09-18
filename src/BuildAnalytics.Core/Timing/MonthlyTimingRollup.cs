using System.Globalization;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Timing;

/// <summary>
/// Pure overall + monthly aggregation. Months are real UTC months ascending, with the
/// unknown bucket last. Averages ignore nulls and are rounded to 2dp at rollup time.
/// </summary>
public static class MonthlyTimingRollup
{
    public const string UnknownMonthKey = "(unknown)";

    public static TimingSummary Summarize(IEnumerable<BuildRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var snapshot = runs as IReadOnlyCollection<BuildRun> ?? runs.ToList();

        var months = snapshot
            .GroupBy(run => MonthKey(run.QueueTime), StringComparer.Ordinal)
            .Select(group => new MonthlyTimingSummary(group.Key, Totals(group)))
            .OrderBy(summary => string.Equals(summary.Month, UnknownMonthKey, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(summary => summary.Month, StringComparer.Ordinal)
            .ToList();

        return new TimingSummary(Totals(snapshot), months);
    }

    /// <summary>UTC year-month key (<c>yyyy-MM</c>), or <see cref="UnknownMonthKey"/> when absent.</summary>
    public static string MonthKey(DateTimeOffset? queueTime)
        => queueTime is { } value
            ? value.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : UnknownMonthKey;

    private static TimingTotals Totals(IEnumerable<BuildRun> runs)
    {
        var runCount = 0;
        var succeeded = 0;
        var failed = 0;
        var partiallySucceeded = 0;
        var canceled = 0;
        var notStarted = 0;
        var waitOverFiveMinutes = 0;
        var queueWaits = new List<double?>();
        var runDurations = new List<double?>();
        var totalDurations = new List<double?>();

        foreach (var run in runs)
        {
            runCount++;

            if (Matches(run.Result, "succeeded")) succeeded++;
            else if (Matches(run.Result, "failed")) failed++;
            else if (Matches(run.Result, "partiallySucceeded")) partiallySucceeded++;
            else if (Matches(run.Result, "canceled")) canceled++;

            if (Matches(run.Status, "notStarted")) notStarted++;

            var timing = TimingCalculator.Calculate(run);
            if (timing.QueueWaitSeconds is { } wait && wait > TimingCalculator.WaitOverFiveMinutesThresholdSeconds)
            {
                waitOverFiveMinutes++;
            }

            queueWaits.Add(timing.QueueWaitSeconds);
            runDurations.Add(timing.RunDurationSeconds);
            totalDurations.Add(timing.TotalDurationSeconds);
        }

        return new TimingTotals(
            runCount,
            succeeded,
            failed,
            partiallySucceeded,
            canceled,
            notStarted,
            waitOverFiveMinutes,
            Average(queueWaits),
            Average(runDurations),
            Average(totalDurations));
    }

    private static bool Matches(string? value, string expected)
        => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static double? Average(IReadOnlyCollection<double?> values)
    {
        double sum = 0;
        var count = 0;

        foreach (var value in values)
        {
            if (value is { } number)
            {
                sum += number;
                count++;
            }
        }

        return count == 0 ? null : Math.Round(sum / count, 2);
    }
}
