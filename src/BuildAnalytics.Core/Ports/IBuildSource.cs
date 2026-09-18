using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Core.Ports;

/// <summary>Paged build-list retrieval with an optional per-run detail fallback.</summary>
public interface IBuildSource
{
    Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken cancellationToken);

    Task<BuildRun> GetDetailAsync(BuildQuery query, int runId, CancellationToken cancellationToken);
}
