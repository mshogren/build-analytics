using System.Text.Json;

sealed class BuildAnalyticsConfig
{
    public string? OrganizationUrl { get; init; }
    public string? Project { get; init; }
    public string? Pat { get; init; }

    public string? OutputRoot { get; init; }
    public int[]? DefinitionIds { get; init; }
    public string[]? DefinitionNames { get; init; }
    public DateTimeOffset? MinTime { get; init; }
    public DateTimeOffset? MaxTime { get; init; }
    public int? ThrottleLimit { get; init; }
    public int? MaxRuns { get; init; }
    public bool? CountOnly { get; init; }

    public string? InputRoot { get; init; }
    public string? OutputPath { get; init; }
    public int[]? PoolIds { get; init; }
    public string[]? ExcludeReasons { get; init; }
    public double? MaxQueueWaitSeconds { get; init; }

    public static BuildAnalyticsConfig Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new BuildAnalyticsConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BuildAnalyticsConfig>(json, SerializerOptions) ?? new BuildAnalyticsConfig();
    }

    public static string DefaultPath => Path.Combine(Environment.CurrentDirectory, "build-analytics.config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
