using System.Text.Json.Serialization;

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
/// List inputs are defensively copied and equality is structural.
/// </summary>
public sealed record Manifest
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public Manifest(
        int schemaVersion,
        string fingerprint,
        ManifestStatus status,
        string? cursor,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? lastError,
        IReadOnlyList<int> failedRunIds,
        IReadOnlyList<int> definitionIds,
        IReadOnlyList<string> definitionNames)
    {
        SchemaVersion = schemaVersion;
        Fingerprint = fingerprint;
        Status = status;
        Cursor = cursor;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastError = lastError;
        FailedRunIds = Copy(failedRunIds);
        DefinitionIds = Copy(definitionIds);
        DefinitionNames = Copy(definitionNames);
    }

    public int SchemaVersion { get; }

    public string Fingerprint { get; }

    public ManifestStatus Status { get; }

    public string? Cursor { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string? LastError { get; }

    public IReadOnlyList<int> FailedRunIds { get; }

    public IReadOnlyList<int> DefinitionIds { get; }

    public IReadOnlyList<string> DefinitionNames { get; }

    public bool Equals(Manifest? other)
        => other is not null
           && SchemaVersion == other.SchemaVersion
           && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal)
           && Status == other.Status
           && string.Equals(Cursor, other.Cursor, StringComparison.Ordinal)
           && CreatedAt == other.CreatedAt
           && UpdatedAt == other.UpdatedAt
           && string.Equals(LastError, other.LastError, StringComparison.Ordinal)
           && FailedRunIds.SequenceEqual(other.FailedRunIds)
           && DefinitionIds.SequenceEqual(other.DefinitionIds)
           && DefinitionNames.SequenceEqual(other.DefinitionNames);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Fingerprint);
        hash.Add(Status);
        hash.Add(Cursor);
        hash.Add(CreatedAt);
        hash.Add(UpdatedAt);
        hash.Add(LastError);
        AddAll(ref hash, FailedRunIds);
        AddAll(ref hash, DefinitionIds);
        AddAll(ref hash, DefinitionNames);
        return hash.ToHashCode();
    }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? values)
        => Array.AsReadOnly((values ?? []).ToArray());

    private static void AddAll<T>(ref HashCode hash, IReadOnlyList<T> values)
    {
        foreach (var value in values)
        {
            hash.Add(value);
        }
    }
}
