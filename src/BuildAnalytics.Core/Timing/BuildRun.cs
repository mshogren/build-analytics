namespace BuildAnalytics.Core.Timing;

/// <summary>A single build run with the timestamps needed for timing analysis.</summary>
public sealed record BuildRun(
    int RunId,
    string? DefinitionName,
    string? Result,
    string? Status,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime)
{
    /// <summary>Seconds between queue time and start time; null when unavailable or skewed.</summary>
    public double? QueueWaitSeconds => Elapsed(QueueTime, StartTime);

    /// <summary>Seconds between start time and finish time; null when unavailable or skewed.</summary>
    public double? RunDurationSeconds => Elapsed(StartTime, FinishTime);

    /// <summary>Seconds between queue time and finish time; null when unavailable or skewed.</summary>
    public double? TotalDurationSeconds => Elapsed(QueueTime, FinishTime);

    private static double? Elapsed(DateTimeOffset? start, DateTimeOffset? end)
        => start is { } from && end is { } to && to >= from ? (to - from).TotalSeconds : null;
}
