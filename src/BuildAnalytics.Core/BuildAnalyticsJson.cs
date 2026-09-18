using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildAnalytics.Core;

/// <summary>
/// The single canonical JSON configuration for build-analytics artifacts.
/// Web defaults (camelCase, case-insensitive reads) plus snake_case string enums.
/// </summary>
public static class BuildAnalyticsJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
