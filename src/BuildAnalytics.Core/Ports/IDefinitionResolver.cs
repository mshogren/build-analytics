using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Resolves a query's raw definition ids/names into the effective, sorted, distinct id set.
/// Kept separate from the frozen <see cref="IBuildSource"/> so the build-list contract stays stable.
/// </summary>
public interface IDefinitionResolver
{
    Task<IReadOnlyList<int>> ResolveAsync(BuildQuery query, CancellationToken cancellationToken);
}
