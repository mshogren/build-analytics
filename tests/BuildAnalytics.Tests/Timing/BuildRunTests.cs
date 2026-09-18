using BuildAnalytics.Core.Timing;

namespace BuildAnalytics.Tests.Timing;

public sealed class BuildRunTests
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QueueWait_is_elapsed_from_queue_to_start()
    {
        var run = Run(queue: T0, start: T0.AddSeconds(90), finish: T0.AddMinutes(10));

        Assert.Equal(90d, run.QueueWaitSeconds);
    }

    [Fact]
    public void RunDuration_is_elapsed_from_start_to_finish()
    {
        var run = Run(queue: T0, start: T0.AddSeconds(90), finish: T0.AddSeconds(390));

        Assert.Equal(300d, run.RunDurationSeconds);
    }

    [Fact]
    public void TotalDuration_is_elapsed_from_queue_to_finish()
    {
        var run = Run(queue: T0, start: T0.AddSeconds(90), finish: T0.AddSeconds(390));

        Assert.Equal(390d, run.TotalDurationSeconds);
    }

    [Fact]
    public void Missing_queue_time_leaves_only_run_duration()
    {
        var run = Run(queue: null, start: T0, finish: T0.AddSeconds(60));

        Assert.Null(run.QueueWaitSeconds);
        Assert.Null(run.TotalDurationSeconds);
        Assert.Equal(60d, run.RunDurationSeconds);
    }

    [Fact]
    public void Missing_start_time_leaves_only_total_duration()
    {
        var run = Run(queue: T0, start: null, finish: T0.AddSeconds(60));

        Assert.Null(run.QueueWaitSeconds);
        Assert.Null(run.RunDurationSeconds);
        Assert.Equal(60d, run.TotalDurationSeconds);
    }

    [Fact]
    public void Missing_finish_time_leaves_only_queue_wait()
    {
        var run = Run(queue: T0, start: T0.AddSeconds(60), finish: null);

        Assert.Null(run.RunDurationSeconds);
        Assert.Null(run.TotalDurationSeconds);
        Assert.Equal(60d, run.QueueWaitSeconds);
    }

    [Fact]
    public void Finish_before_start_yields_null_never_negative()
    {
        var run = Run(queue: T0, start: T0.AddMinutes(5), finish: T0.AddMinutes(4));

        Assert.Null(run.RunDurationSeconds);
    }

    [Fact]
    public void Queue_after_start_yields_null_never_negative()
    {
        var run = Run(queue: T0.AddMinutes(5), start: T0, finish: T0.AddMinutes(10));

        Assert.Null(run.QueueWaitSeconds);
    }

    [Fact]
    public void Equal_timestamps_yield_zero()
    {
        var run = Run(queue: T0, start: T0, finish: T0);

        Assert.Equal(0d, run.QueueWaitSeconds);
        Assert.Equal(0d, run.RunDurationSeconds);
        Assert.Equal(0d, run.TotalDurationSeconds);
    }

    [Fact]
    public void Offsets_are_normalized_when_computing_durations()
    {
        var queue = new DateTimeOffset(2024, 3, 10, 14, 0, 0, TimeSpan.FromHours(2));
        var start = new DateTimeOffset(2024, 3, 10, 12, 1, 0, TimeSpan.Zero);

        var run = Run(queue: queue, start: start, finish: start.AddSeconds(60));

        Assert.Equal(60d, run.QueueWaitSeconds);
    }

    private static BuildRun Run(DateTimeOffset? queue, DateTimeOffset? start, DateTimeOffset? finish)
        => new(RunId: 1, DefinitionName: "ci", Result: "succeeded", Status: "completed", queue, start, finish);
}
