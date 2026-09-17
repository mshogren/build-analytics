using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsExportOptionsTests
{
    [Fact]
    public void Parse_UsesConfig_EnvOverridesConfig_AndCliOverridesEnv()
    {
        using var temp = new TempDirectory();
        var configPath = System.IO.Path.Combine(temp.Path, "build-analytics.config.json");
        File.WriteAllText(configPath, """
        {
          "organizationUrl": "https://config.example",
          "project": "config-project",
          "pat": "config-pat",
          "outputRoot": "/config/output",
          "definitionIds": [1, 2],
          "definitionNames": ["config-*"],
          "minTime": "2024-01-01T00:00:00Z",
          "maxTime": "2024-02-01T00:00:00Z",
          "throttleLimit": 4,
          "maxRuns": 88,
          "countOnly": false
        }
        """);

        using var env = new EnvironmentScope()
            .Set("BUILD_ANALYTICS_CONFIG", configPath)
            .Set("AZDO_PAT", "env-pat")
            .Set("BUILD_ANALYTICS_OUTPUT_ROOT", "/env/output")
            .Set("BUILD_ANALYTICS_DEFINITION_IDS", "9,10")
            .Set("BUILD_ANALYTICS_THROTTLE_LIMIT", "7");

        var options = BuildAnalyticsExportOptions.Parse(new[]
        {
            "--project", "cli-project",
            "--throttle-limit", "11"
        });

        Assert.Equal("https://config.example", options.OrganizationUrl);
        Assert.Equal("cli-project", options.Project);
        Assert.Equal("env-pat", options.Pat);
        Assert.Equal("/env/output", options.OutputRoot);
        Assert.Equal(new[] { 9, 10 }, options.DefinitionIds);
        Assert.Equal(new[] { "config-*" }, options.DefinitionNames);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), options.MinTime);
        Assert.Equal(DateTimeOffset.Parse("2024-02-01T00:00:00Z"), options.MaxTime);
        Assert.Equal(11, options.ThrottleLimit);
        Assert.Equal(88, options.MaxRuns);
        Assert.False(options.CountOnly);
    }
}
