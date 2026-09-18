namespace BuildAnalytics.Core.Query;

/// <summary>Whether per-run detail calls are allowed when list fields are insufficient.</summary>
public enum DetailPolicy
{
    ListOnly,
    FillMissing
}

/// <summary>
/// The effective query. Raw <see cref="DefinitionIds"/>/<see cref="DefinitionNames"/> are
/// informational only; identity is <see cref="ResolvedDefinitionIds"/>.
/// </summary>
public sealed record BuildQuery(
    string Organization,
    string Project,
    DateTimeOffset? MinTime,
    DateTimeOffset? MaxTime,
    IReadOnlyList<int> ResolvedDefinitionIds,
    DetailPolicy DetailPolicy,
    string ApiVersion)
{
    public IReadOnlyList<int> DefinitionIds { get; init; } = [];

    public IReadOnlyList<string> DefinitionNames { get; init; } = [];
}
