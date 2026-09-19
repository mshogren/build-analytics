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
/// Retrieval progress for one output root. There is no listing cursor (ADR-108); the durable
/// progress marker is the set of runs in <c>runs.jsonl</c> (ADR-109).
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
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? lastError,
        IReadOnlyList<int> failedRunIds)
    {
        SchemaVersion = schemaVersion;
        Fingerprint = fingerprint;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        LastError = lastError;
        FailedRunIds = Copy(failedRunIds);
    }

    public int SchemaVersion { get; }

    public string Fingerprint { get; }

    public ManifestStatus Status { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string? LastError { get; }

    public IReadOnlyList<int> FailedRunIds { get; }

    public bool Equals(Manifest? other)
        => other is not null
           && SchemaVersion == other.SchemaVersion
           && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal)
           && Status == other.Status
           && CreatedAt == other.CreatedAt
           && UpdatedAt == other.UpdatedAt
           && string.Equals(LastError, other.LastError, StringComparison.Ordinal)
           && FailedRunIds.SequenceEqual(other.FailedRunIds);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Fingerprint);
        hash.Add(Status);
        hash.Add(CreatedAt);
        hash.Add(UpdatedAt);
        hash.Add(LastError);
        AddAll(ref hash, FailedRunIds);
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
