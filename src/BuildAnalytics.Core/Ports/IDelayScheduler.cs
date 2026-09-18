namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Delay seam used by retry/backoff so waits can be deterministic in tests.
/// This is Core's only timing seam: adapters own wall-clock timestamps
/// (CreatedAt/UpdatedAt/FetchedAt) and any ambient time source.
/// </summary>
public interface IDelayScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
