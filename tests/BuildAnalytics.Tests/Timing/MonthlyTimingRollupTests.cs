using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Timing;

public sealed class MonthlyTimingRollupTests
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_input_produces_zeroed_overall_and_no_months()
    {
        var summary = MonthlyTimingRollup.Summarize([]);

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

        var overall = MonthlyTimingRollup.Summarize(runs).Overall;

        Assert.Equal(6, overall.RunCount);
        Assert.Equal(3, overall.SucceededCount);
        Assert.Equal(1, overall.FailedCount);
        Assert.Equal(1, overall.PartiallySucceededCount);
        Assert.Equal(1, overall.CanceledCount);
        Assert.Equal(1, overall.NotStartedCount);
    }

    [Fact]
    public void Wait_over_five_minutes_is_strictly_greater_than_300_on_raw_seconds()
    {
        var runs = new[]
        {
            Run(queueWaitSeconds: 300),
            Run(queueWaitSeconds: 300.5),
            Run(queueWaitSeconds: 301),
            Run(queueWaitSeconds: 0),
            Run(omitTimestamps: true)
        };

        var overall = MonthlyTimingRollup.Summarize(runs).Overall;

        Assert.Equal(2, overall.WaitOverFiveMinutesCount);
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

        var overall = MonthlyTimingRollup.Summarize(runs).Overall;

        Assert.Equal(150d, overall.AverageQueueWaitSeconds);
        Assert.Equal(150d, overall.AverageRunDurationSeconds);
        Assert.Equal(300d, overall.AverageTotalDurationSeconds);
    }

    [Theory]
    [InlineData(100d, 101d, 101d, 100.67d)]
    [InlineData(100.004d, 100.004d, 100.004d, 100.0d)]
    [InlineData(100.006d, 100.006d, 100.006d, 100.01d)]
    [InlineData(100.125d, 100.125d, 100.125d, 100.13d)]
    [InlineData(100.375d, 100.375d, 100.375d, 100.38d)]
    [InlineData(100.625d, 100.625d, 100.625d, 100.63d)]
    public void Averages_round_raw_mean_to_two_decimals(double first, double second, double third, double expected)
    {
        var runs = new[] { Run(queueWaitSeconds: first), Run(queueWaitSeconds: second), Run(queueWaitSeconds: third) };

        var overall = MonthlyTimingRollup.Summarize(runs).Overall;

        Assert.Equal(expected, overall.AverageQueueWaitSeconds!.Value);
    }

    [Fact]
    public void Averages_per_metric_nulls_are_independent()
    {
        var queueOnly = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(100), finishTime: null);
        var finishOnly = TestRuns.Create(queueTime: null, startTime: T0, finishTime: T0.AddSeconds(200));

        var overall = MonthlyTimingRollup.Summarize([queueOnly, finishOnly]).Overall;

        Assert.Equal(100d, overall.AverageQueueWaitSeconds);
        Assert.Equal(200d, overall.AverageRunDurationSeconds);
        Assert.Null(overall.AverageTotalDurationSeconds);
    }

    [Theory]
    [InlineData(300.0000001d, 1)]
    [InlineData(300.004d, 1)]
    [InlineData(299.999d, 0)]
    public void WaitOverFiveMinutes_compares_raw_not_rounded(double waitSeconds, int expected)
    {
        var overall = MonthlyTimingRollup.Summarize([Run(queueWaitSeconds: waitSeconds)]).Overall;

        Assert.Equal(expected, overall.WaitOverFiveMinutesCount);
    }

    [Fact]
    public void Result_and_status_null_or_unknown_counts_as_zero_no_throw()
    {
        var runs = new[]
        {
            TestRuns.Create(result: null, status: null),
            TestRuns.Create(result: "mystery", status: "mystery")
        };

        var overall = MonthlyTimingRollup.Summarize(runs).Overall;

        Assert.Equal(2, overall.RunCount);
        Assert.Equal(0, overall.SucceededCount);
        Assert.Equal(0, overall.FailedCount);
        Assert.Equal(0, overall.PartiallySucceededCount);
        Assert.Equal(0, overall.CanceledCount);
        Assert.Equal(0, overall.NotStartedCount);
    }

    [Fact]
    public void Different_offsets_same_utc_month_group_together()
    {
        var plusTwo = new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2)); // 2024-02-29T22:30Z
        var utc = new DateTimeOffset(2024, 2, 29, 23, 0, 0, TimeSpan.Zero);

        var summary = MonthlyTimingRollup.Summarize([Run(queueTime: plusTwo), Run(queueTime: utc)]);

        var month = Assert.Single(summary.Months);
        Assert.Equal("2024-02", month.Month);
        Assert.Equal(2, month.Totals.RunCount);
    }

    [Fact]
    public void Averages_are_null_when_all_values_are_null()
    {
        var overall = MonthlyTimingRollup.Summarize([Run(omitTimestamps: true)]).Overall;

        Assert.Null(overall.AverageQueueWaitSeconds);
        Assert.Null(overall.AverageRunDurationSeconds);
        Assert.Null(overall.AverageTotalDurationSeconds);
    }

    [Fact]
    public void Months_are_grouped_by_utc_month_and_unknown_bucket_sorts_last()
    {
        var runs = new[]
        {
            Run(queueTime: T0),
            Run(queueTime: T0.AddDays(1)),
            Run(queueTime: new DateTimeOffset(2024, 2, 29, 23, 0, 0, TimeSpan.Zero)),
            Run(omitTimestamps: true)
        };

        var summary = MonthlyTimingRollup.Summarize(runs);

        Assert.Equal(["2024-02", "2024-03", "(unknown)"], summary.Months.Select(m => m.Month));
        Assert.Equal(1, summary.Months.Single(m => m.Month == "2024-02").Totals.RunCount);
        Assert.Equal(2, summary.Months.Single(m => m.Month == "2024-03").Totals.RunCount);
        Assert.Equal(1, summary.Months.Single(m => m.Month == MonthlyTimingRollup.UnknownMonthKey).Totals.RunCount);
    }

    [Fact]
    public void Month_grouping_uses_utc_even_when_queue_time_has_an_offset()
    {
        // 2024-03-01T00:30+02:00 is 2024-02-29T22:30Z.
        var queue = new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2));

        var summary = MonthlyTimingRollup.Summarize([Run(queueTime: queue)]);

        var month = Assert.Single(summary.Months);
        Assert.Equal("2024-02", month.Month);
    }

    [Fact]
    public void Month_anchor_is_queue_time_only_and_ignores_start_time()
    {
        var queue = new DateTimeOffset(2024, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2024, 3, 2, 0, 0, 0, TimeSpan.Zero);

        var summary = MonthlyTimingRollup.Summarize([Run(queueTime: queue, startTime: start)]);

        Assert.Equal("2024-02", Assert.Single(summary.Months).Month);
    }

    [Fact]
    public void Missing_queue_time_is_unknown_even_when_start_time_is_present()
    {
        var summary = MonthlyTimingRollup.Summarize([TestRuns.Create(queueTime: null, startTime: T0, finishTime: T0.AddSeconds(10))]);

        Assert.Equal(MonthlyTimingRollup.UnknownMonthKey, Assert.Single(summary.Months).Month);
    }

    [Fact]
    public void Monthly_totals_match_a_single_month_overall()
    {
        var runs = new[]
        {
            Run("failed", "completed", queueWaitSeconds: 301),
            Run("succeeded", "completed", queueWaitSeconds: 10)
        };

        var summary = MonthlyTimingRollup.Summarize(runs);

        Assert.Equal(summary.Overall, Assert.Single(summary.Months).Totals);
    }

    [Fact]
    public void Summarize_RepeatedCalls_ProduceEqualResult()
    {
        var runs = new[] { Run(queueTime: T0), Run(omitTimestamps: true) };

        Assert.Equal(MonthlyTimingRollup.Summarize(runs), MonthlyTimingRollup.Summarize(runs));
    }

    [Fact]
    public void Averages_round_midpoints_away_from_zero()
    {
        var overall = MonthlyTimingRollup.Summarize([Run(queueWaitSeconds: 0.125)]).Overall;

        Assert.Equal(0.13d, overall.AverageQueueWaitSeconds!.Value);
    }

    [Fact]
    public void Incomplete_runs_are_counted_but_excluded_from_averages()
    {
        var incomplete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(5), finishTime: null);
        var complete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(10), finishTime: T0.AddSeconds(30));

        var summary = MonthlyTimingRollup.Summarize([incomplete, complete]);

        Assert.Equal(2, summary.Overall.RunCount);
        Assert.Equal(2, Assert.Single(summary.Months).Totals.RunCount);
        Assert.Equal(7.5d, summary.Overall.AverageQueueWaitSeconds);
        Assert.Equal(20d, summary.Overall.AverageRunDurationSeconds);
        Assert.Equal(30d, summary.Overall.AverageTotalDurationSeconds);
    }

    [Fact]
    public void TimingSummary_defensively_copies_the_months_list()
    {
        var emptyTotals = new TimingTotals(0, 0, 0, 0, 0, 0, 0, null, null, null);
        var source = new List<MonthlyTimingSummary> { new("2024-03", emptyTotals) };
        var summary = new TimingSummary(emptyTotals, source);
        var before = summary;

        source.Add(new MonthlyTimingSummary("2024-04", emptyTotals));

        Assert.Single(summary.Months);
        Assert.Equal(before, summary);
    }

    [Fact]
    public void TimingSummary_defensive_copy_survives_caller_mutation()
    {
        var emptyTotals = new TimingTotals(0, 0, 0, 0, 0, 0, 0, null, null, null);
        var source = new List<MonthlyTimingSummary> { new("2024-03", emptyTotals) };
        var summary = new TimingSummary(emptyTotals, source);
        var hash = summary.GetHashCode();

        source.Clear();
        source.Add(new MonthlyTimingSummary("2024-04", emptyTotals));

        Assert.Single(summary.Months);
        Assert.Equal("2024-03", summary.Months[0].Month);
        Assert.Equal(hash, summary.GetHashCode());
    }

    [Fact]
    public void Summarize_result_is_unaffected_by_mutating_the_input_after_call()
    {
        var runs = new List<BuildRun> { Run(queueTime: T0), Run(queueTime: T0.AddDays(1)) };
        var summary = MonthlyTimingRollup.Summarize(runs);
        var before = summary;

        runs.Clear();

        Assert.Equal(2, summary.Overall.RunCount);
        Assert.Single(summary.Months);
        Assert.Equal(before, summary);
    }

    [Fact]
    public void Summarize_RunCount_IncludesIncompleteRuns_NullFinishTime()
    {
        var incomplete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(5), finishTime: null);

        var summary = MonthlyTimingRollup.Summarize([incomplete, incomplete, incomplete]);

        Assert.Equal(3, summary.Overall.RunCount);
    }

    [Fact]
    public void Summarize_MonthBuckets_IncludeIncompleteRuns()
    {
        var incomplete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(5), finishTime: null);

        var summary = MonthlyTimingRollup.Summarize([incomplete]);

        Assert.Equal(1, Assert.Single(summary.Months).Totals.RunCount);
    }

    [Fact]
    public void Summarize_Averages_SkipNullMetrics_ButCountsDoNot()
    {
        var incomplete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(5), finishTime: null);
        var complete = TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(10), finishTime: T0.AddSeconds(30));

        var overall = MonthlyTimingRollup.Summarize([incomplete, complete]).Overall;

        Assert.Equal(2, overall.RunCount);
        Assert.Equal(7.5d, overall.AverageQueueWaitSeconds);
        Assert.Equal(20d, overall.AverageRunDurationSeconds);
        Assert.Equal(30d, overall.AverageTotalDurationSeconds);
    }

    [Fact]
    public void Summarize_IncompleteWithoutQueueTime_GoesToUnknownBucket_StillCounted()
    {
        var incomplete = TestRuns.Create(queueTime: null, startTime: T0, finishTime: null);

        var summary = MonthlyTimingRollup.Summarize([incomplete]);

        var month = Assert.Single(summary.Months);
        Assert.Equal(MonthlyTimingRollup.UnknownMonthKey, month.Month);
        Assert.Equal(1, month.Totals.RunCount);
    }

    private static BuildRun Run(
        string result = "succeeded",
        string status = "completed",
        DateTimeOffset? queueTime = null,
        double? queueWaitSeconds = null,
        double? runDurationSeconds = null,
        DateTimeOffset? startTime = null,
        bool omitTimestamps = false)
    {
        if (omitTimestamps)
        {
            return TestRuns.Create(result: result, status: status);
        }

        var queue = queueTime ?? T0;
        var start = startTime ?? (queueWaitSeconds is { } wait ? queue.AddSeconds(wait) : queue);
        var finish = start.AddSeconds(runDurationSeconds ?? 0);
        return TestRuns.Create(result: result, status: status, queueTime: queue, startTime: start, finishTime: finish);
    }
}
