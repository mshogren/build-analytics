using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildAnalytics.Core;

/// <summary>
/// The single canonical JSON configuration for build-analytics artifacts.
/// Web defaults (camelCase, case-insensitive reads) plus snake_case string enums.
/// Adapters and tests must consume <see cref="Options"/> and never construct their own.
/// </summary>
public static class BuildAnalyticsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
