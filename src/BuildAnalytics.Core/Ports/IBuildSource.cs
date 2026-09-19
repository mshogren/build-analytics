using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Core.Ports;

/// <summary>Paged build-list retrieval. The list payload is the only data source.</summary>
public interface IBuildSource
{
    Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken cancellationToken);
}
