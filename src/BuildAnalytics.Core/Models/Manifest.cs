namespace BuildAnalytics.Core.Models;

public enum ManifestStatus
{
    Pending,
    InProgress,
    Completed,
    Paused,
    Failed
}

/// <summary>
/// Retrieval progress for one output root. The cursor is the authoritative progress field.
/// </summary>
public sealed record Manifest(
    int SchemaVersion,
    string Fingerprint,
    ManifestStatus Status,
    string? Cursor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    IReadOnlyList<int> FailedRunIds,
    IReadOnlyList<int> DefinitionIds,
    IReadOnlyList<string> DefinitionNames)
{
    public const int CurrentSchemaVersion = 1;
}
