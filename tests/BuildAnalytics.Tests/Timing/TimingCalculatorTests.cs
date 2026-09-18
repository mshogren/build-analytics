using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Timing;

public sealed class TimingCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Queue_wait_is_elapsed_from_queue_to_start()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(90), finishTime: T0.AddMinutes(10)));

        Assert.Equal(90d, timing.QueueWaitSeconds);
    }

    [Fact]
    public void Run_duration_is_elapsed_from_start_to_finish()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(90), finishTime: T0.AddSeconds(390)));

        Assert.Equal(300d, timing.RunDurationSeconds);
    }

    [Fact]
    public void Total_duration_is_elapsed_from_queue_to_finish()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(90), finishTime: T0.AddSeconds(390)));

        Assert.Equal(390d, timing.TotalDurationSeconds);
    }

    [Fact]
    public void Missing_queue_time_leaves_only_run_duration()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: null, startTime: T0, finishTime: T0.AddSeconds(60)));

        Assert.Null(timing.QueueWaitSeconds);
        Assert.Null(timing.TotalDurationSeconds);
        Assert.Equal(60d, timing.RunDurationSeconds);
    }

    [Fact]
    public void Missing_start_time_leaves_only_total_duration()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: null, finishTime: T0.AddSeconds(60)));

        Assert.Null(timing.QueueWaitSeconds);
        Assert.Null(timing.RunDurationSeconds);
        Assert.Equal(60d, timing.TotalDurationSeconds);
    }

    [Fact]
    public void Missing_finish_time_leaves_only_queue_wait()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddSeconds(60), finishTime: null));

        Assert.Null(timing.RunDurationSeconds);
        Assert.Null(timing.TotalDurationSeconds);
        Assert.Equal(60d, timing.QueueWaitSeconds);
    }

    [Fact]
    public void Finish_before_start_yields_null_never_negative()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddMinutes(5), finishTime: T0.AddMinutes(4)));

        Assert.Null(timing.RunDurationSeconds);
    }

    [Fact]
    public void Queue_after_start_yields_null_never_negative()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0.AddMinutes(5), startTime: T0, finishTime: T0.AddMinutes(10)));

        Assert.Null(timing.QueueWaitSeconds);
    }

    [Fact]
    public void Equal_timestamps_yield_zero()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0, finishTime: T0));

        Assert.Equal(0d, timing.QueueWaitSeconds);
        Assert.Equal(0d, timing.RunDurationSeconds);
        Assert.Equal(0d, timing.TotalDurationSeconds);
    }

    [Fact]
    public void Sub_millisecond_precision_is_preserved()
    {
        var start = T0;
        var finish = start.AddTicks(12_345_678); // 1.2345678s
        var timing = TimingCalculator.Calculate(TestRuns.Create(queueTime: T0, startTime: start, finishTime: finish));

        Assert.Equal(1.2345678d, timing.RunDurationSeconds!.Value, precision: 9);
    }

    [Fact]
    public void Raw_seconds_preserve_fractional_precision()
    {
        var start = T0;
        var queue = start.AddSeconds(-0.125);
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: queue, startTime: start, finishTime: start.AddSeconds(1)));

        Assert.Equal(0.125d, timing.QueueWaitSeconds!.Value, precision: 9);
    }

    [Fact]
    public void All_timestamps_null_yield_all_metrics_null()
    {
        var timing = TimingCalculator.Calculate(TestRuns.Create(queueTime: null, startTime: null, finishTime: null));

        Assert.Null(timing.QueueWaitSeconds);
        Assert.Null(timing.RunDurationSeconds);
        Assert.Null(timing.TotalDurationSeconds);
    }

    [Fact]
    public void Finish_before_queue_yields_null_total_never_negative()
    {
        var queue = T0.AddMinutes(10);
        var finish = T0.AddMinutes(5);
        var timing = TimingCalculator.Calculate(TestRuns.Create(queueTime: queue, startTime: null, finishTime: finish));

        Assert.Null(timing.TotalDurationSeconds);
    }

    [Fact]
    public void Skew_in_one_metric_does_not_poison_others()
    {
        var timing = TimingCalculator.Calculate(
            TestRuns.Create(queueTime: T0, startTime: T0.AddMinutes(10), finishTime: T0.AddMinutes(5)));

        Assert.Equal(600d, timing.QueueWaitSeconds);
        Assert.Null(timing.RunDurationSeconds);
        Assert.Equal(300d, timing.TotalDurationSeconds);
    }

    [Fact]
    public void Offsets_are_normalized_when_computing_durations()
    {
        var queue = new DateTimeOffset(2024, 3, 10, 14, 0, 0, TimeSpan.FromHours(2));
        var start = new DateTimeOffset(2024, 3, 10, 12, 1, 0, TimeSpan.Zero);

        var timing = TimingCalculator.Calculate(TestRuns.Create(queueTime: queue, startTime: start, finishTime: start.AddSeconds(60)));

        Assert.Equal(60d, timing.QueueWaitSeconds);
    }
}
