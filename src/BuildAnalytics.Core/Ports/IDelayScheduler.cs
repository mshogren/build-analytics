namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Delay seam used by retry/backoff so waits can be deterministic in tests.
/// Timestamps use the BCL <see cref="TimeProvider"/> seam.
/// </summary>
public interface IDelayScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
