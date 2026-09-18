namespace BuildAnalytics.Core.Timing;

/// <summary>Raw per-run durations in seconds. Null means unavailable or skewed.</summary>
public sealed record RunTiming(
    double? QueueWaitSeconds,
    double? RunDurationSeconds,
    double? TotalDurationSeconds);
