using System.Globalization;

namespace BuildAnalytics.Core.Timing;

/// <summary>Pure aggregation of <see cref="BuildRun"/> timing data. Performs no IO and reads no ambient clock.</summary>
public static class TimingSummarizer
{
    /// <summary>Runs waiting strictly more than this many seconds are counted as "wait over 5 min".</summary>
    public const double WaitOverFiveMinutesThresholdSeconds = 300d;

    /// <summary>Month key used for runs without a queue time.</summary>
    public const string UnknownMonthKey = "(unknown)";

    public static TimingSummary Summarize(IEnumerable<BuildRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var snapshot = runs as IReadOnlyCollection<BuildRun> ?? runs.ToList();

        var months = snapshot
            .GroupBy(run => MonthKey(run.QueueTime), StringComparer.Ordinal)
            .Select(group => new MonthlyTimingSummary(group.Key, SummarizeTotals(group)))
            .OrderBy(summary => string.Equals(summary.Month, UnknownMonthKey, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(summary => summary.Month, StringComparer.Ordinal)
            .ToList();

        return new TimingSummary(SummarizeTotals(snapshot), months);
    }

    /// <summary>UTC year-month key (<c>yyyy-MM</c>) for a timestamp, or <see cref="UnknownMonthKey"/>.</summary>
    public static string MonthKey(DateTimeOffset? queueTime)
        => queueTime is { } value
            ? value.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : UnknownMonthKey;

    private static TimingTotals SummarizeTotals(IEnumerable<BuildRun> runs)
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
            if (run.QueueWaitSeconds is { } wait && wait > WaitOverFiveMinutesThresholdSeconds) waitOverFiveMinutes++;

            queueWaits.Add(run.QueueWaitSeconds);
            runDurations.Add(run.RunDurationSeconds);
            totalDurations.Add(run.TotalDurationSeconds);
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
