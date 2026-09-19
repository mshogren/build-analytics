namespace BuildAnalytics.Core.Models;

/// <summary>
/// Flat raw run contract. ADO fields outside this record are intentionally dropped.
/// Durations are not stored here; they are derived by the pure timing calculator.
/// </summary>
public sealed record BuildRun(
    int SchemaVersion,
    DateTimeOffset FetchedAt,
    int Id,
    int? DefinitionId,
    string? DefinitionName,
    string? BuildNumber,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    string? Status,
    string? Result,
    string? Reason,
    int? PoolId,
    string? PoolName,
    string? SourceBranch)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One page of build-list results plus the continuation token for the next page.
/// <see cref="TotalCount"/> mirrors the ADO <c>count</c> field (total matching the query) when present.
/// </summary>
public sealed record BuildPage(IReadOnlyList<BuildRun> Runs, string? ContinuationToken, int? TotalCount = null);
