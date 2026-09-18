using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Ports;

/// <summary>Stores and reads raw run payloads, keyed by run id.</summary>
public interface IRunStore
{
    Task WriteAsync(BuildRun run, CancellationToken cancellationToken);

    Task<BuildRun?> TryReadAsync(int runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> ListRunIdsAsync(CancellationToken cancellationToken);
}
