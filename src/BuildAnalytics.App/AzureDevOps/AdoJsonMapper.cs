using System.Globalization;
using System.Text.Json;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Maps a flat ADO build JSON object onto <see cref="BuildRun"/>. Only contract fields
/// are read; nested <c>definition</c>/<c>queue.pool</c> are flattened and every other
/// ADO field is dropped.
/// </summary>
public static class AdoJsonMapper
{
    /// <summary>Maps one build object. A missing/non-positive <c>id</c> yields <c>Id = 0</c>.</summary>
    public static BuildRun MapBuild(JsonElement element, DateTimeOffset fetchedAt)
    {
        var definition = Nested(element, "definition");
        var pool = Nested(Nested(element, "queue"), "pool");

        return new BuildRun(
            SchemaVersion: BuildRun.CurrentSchemaVersion,
            FetchedAt: fetchedAt,
            Id: GetInt(element, "id") ?? 0,
            DefinitionId: GetInt(definition, "id"),
            DefinitionName: GetString(definition, "name"),
            BuildNumber: GetString(element, "buildNumber"),
            QueueTime: GetDate(element, "queueTime"),
            StartTime: GetDate(element, "startTime"),
            FinishTime: GetDate(element, "finishTime"),
            Status: GetString(element, "status"),
            Result: GetString(element, "result"),
            Reason: GetString(element, "reason"),
            PoolId: GetInt(pool, "id"),
            PoolName: GetString(pool, "name"),
            SourceBranch: GetString(element, "sourceBranch"));
    }

    /// <summary>Enumerates <c>value</c> (or a bare array). Yields nothing for other shapes.</summary>
    public static IEnumerable<JsonElement> ExtractItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static JsonElement? Nested(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? Nested(JsonElement? element, string property)
        => element is { } value ? Nested(value, property) : null;

    public static string? GetString(JsonElement? element, string property)
    {
        if (element is not { } container
            || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }

    public static int? GetInt(JsonElement? element, string property)
    {
        if (element is not { } container
            || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? GetDate(JsonElement element, string property)
    {
        var raw = GetString(element, property);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
