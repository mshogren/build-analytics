using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Core.Ports;

/// <summary>
/// Resolves definition name patterns into effective, sorted, distinct ids.
/// Kept separate from the frozen <see cref="IBuildSource"/> so the build-list contract stays stable (ADR-37).
/// </summary>
public interface IDefinitionResolver
{
    Task<IReadOnlyList<int>> ResolveAsync(
        BuildQuery query,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken);
}
