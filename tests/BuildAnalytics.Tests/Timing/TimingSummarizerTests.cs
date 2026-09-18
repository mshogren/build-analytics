using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Timing;

public sealed class TimingSummarizerTests
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_input_produces_zeroed_overall_and_no_months()
    {
        var summary = TimingSummarizer.Summarize([]);

        Assert.Equal(0, summary.Overall.RunCount);
        Assert.Equal(0, summary.Overall.SucceededCount);
        Assert.Equal(0, summary.Overall.FailedCount);
        Assert.Equal(0, summary.Overall.WaitOverFiveMinutesCount);
        Assert.Null(summary.Overall.AverageQueueWaitSeconds);
        Assert.Null(summary.Overall.AverageRunDurationSeconds);
        Assert.Null(summary.Overall.AverageTotalDurationSeconds);
        Assert.Empty(summary.Months);
    }

    [Fact]
    public void Result_and_status_counts_are_case_insensitive()
    {
        var runs = new[]
        {
            Run("Succeeded", "Completed"),
            Run("succeeded", "completed"),
            Run("FAILED", "completed"),
            Run("PartiallySucceeded", "completed"),
            Run("canceled", "completed"),
            Run("succeeded", "NotStarted")
        };

        var overall = TimingSummarizer.Summarize(runs).Overall;

        Assert.Equal(6, overall.RunCount);
        Assert.Equal(3, overall.SucceededCount);
        Assert.Equal(1, overall.FailedCount);
        Assert.Equal(1, overall.PartiallySucceededCount);
        Assert.Equal(1, overall.CanceledCount);
        Assert.Equal(1, overall.NotStartedCount);
    }

    [Fact]
    public void Wait_over_five_minutes_is_strictly_greater_than_300()
    {
        var runs = new[]
        {
            Run(queueWaitSeconds: 300),
            Run(queueWaitSeconds: 301),
            Run(queueWaitSeconds: 0),
            Run(omitTimestamps: true)
        };

        var overall = TimingSummarizer.Summarize(runs).Overall;

        Assert.Equal(1, overall.WaitOverFiveMinutesCount);
    }

    [Fact]
    public void Averages_ignore_nulls()
    {
        var runs = new[]
        {
            Run(queueWaitSeconds: 100, runDurationSeconds: 100),
            Run(queueWaitSeconds: 200, runDurationSeconds: 200),
            Run(omitTimestamps: true)
        };

        var overall = TimingSummarizer.Summarize(runs).Overall;

        Assert.Equal(150d, overall.AverageQueueWaitSeconds);
        Assert.Equal(150d, overall.AverageRunDurationSeconds);
        Assert.Equal(300d, overall.AverageTotalDurationSeconds);
    }

    [Fact]
    public void Averages_are_null_when_all_values_are_null()
    {
        var runs = new[] { Run(omitTimestamps: true) };

        var overall = TimingSummarizer.Summarize(runs).Overall;

        Assert.Null(overall.AverageQueueWaitSeconds);
        Assert.Null(overall.AverageRunDurationSeconds);
        Assert.Null(overall.AverageTotalDurationSeconds);
    }

    [Fact]
    public void Months_are_grouped_by_utc_month_and_unknown_bucket_is_used_for_missing_queue_time()
    {
        var runs = new[]
        {
            Run(queueTime: T0),
            Run(queueTime: T0.AddDays(1)),
            Run(queueTime: new DateTimeOffset(2024, 2, 29, 23, 0, 0, TimeSpan.Zero)),
            Run(omitTimestamps: true)
        };

        var summary = TimingSummarizer.Summarize(runs);

        Assert.Equal(["2024-02", "2024-03", "(unknown)"], summary.Months.Select(m => m.Month));
        Assert.Equal(1, summary.Months.Single(m => m.Month == "2024-02").Totals.RunCount);
        Assert.Equal(2, summary.Months.Single(m => m.Month == "2024-03").Totals.RunCount);
        Assert.Equal(1, summary.Months.Single(m => m.Month == TimingSummarizer.UnknownMonthKey).Totals.RunCount);
    }

    [Fact]
    public void Month_grouping_uses_utc_even_when_queue_time_has_an_offset()
    {
        // 2024-03-01T00:30+02:00 is 2024-02-29T22:30Z.
        var queue = new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2));

        var summary = TimingSummarizer.Summarize([Run(queueTime: queue)]);

        var month = Assert.Single(summary.Months);
        Assert.Equal("2024-02", month.Month);
    }

    [Fact]
    public void Monthly_totals_match_a_single_month_overall()
    {
        var runs = new[]
        {
            Run("failed", "completed", queueWaitSeconds: 301),
            Run("succeeded", "completed", queueWaitSeconds: 10)
        };

        var summary = TimingSummarizer.Summarize(runs);

        var month = Assert.Single(summary.Months);
        Assert.Equal(summary.Overall, month.Totals);
    }

    [Fact]
    public void Summarize_is_deterministic_and_does_not_depend_on_ambient_clock()
    {
        var runs = new[] { Run(queueTime: T0), Run(omitTimestamps: true) };

        var first = TimingSummarizer.Summarize(runs);
        var second = TimingSummarizer.Summarize(runs);

        Assert.Equal(first, second);
    }

    private static BuildRun Run(
        string result = "succeeded",
        string status = "completed",
        DateTimeOffset? queueTime = null,
        int? queueWaitSeconds = null,
        int? runDurationSeconds = null,
        bool omitTimestamps = false)
    {
        if (omitTimestamps)
        {
            return new BuildRun(1, "ci", result, status, null, null, null);
        }

        var queue = queueTime ?? T0;
        var start = queue.AddSeconds(queueWaitSeconds ?? 0);
        var finish = start.AddSeconds(runDurationSeconds ?? 0);
        return new BuildRun(1, "ci", result, status, queue, start, finish);
    }
}
