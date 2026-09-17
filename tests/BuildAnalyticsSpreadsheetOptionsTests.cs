using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsSpreadsheetOptionsTests
{
    [Fact]
    public void Parse_UsesConfig_EnvOverridesConfig_AndCliOverridesEnv()
    {
        using var temp = new TempDirectory();
        var configPath = System.IO.Path.Combine(temp.Path, "build-analytics.config.json");
        File.WriteAllText(configPath, """
        {
          "inputRoot": "/config/input",
          "outputPath": "/config/output.xlsx",
          "poolIds": [1, 2],
          "excludeReasons": ["schedule"],
          "maxQueueWaitSeconds": 7200
        }
        """);

        using var env = new EnvironmentScope()
            .Set("BUILD_ANALYTICS_CONFIG", configPath)
            .Set("BUILD_ANALYTICS_INPUT_ROOT", "/env/input")
            .Set("BUILD_ANALYTICS_OUTPUT_PATH", "/env/output.xlsx")
            .Set("BUILD_ANALYTICS_POOL_IDS", "9")
            .Set("BUILD_ANALYTICS_EXCLUDE_REASONS", "manual,other")
            .Set("BUILD_ANALYTICS_MAX_QUEUE_WAIT_SECONDS", "3600");

        var options = BuildAnalyticsSpreadsheetOptions.Parse(new[]
        {
            "--output-path", "/cli/output.xlsx",
            "--input-root", "/cli/input",
            "--pool-id", "8,10",
            "--exclude-reason", "schedule,triggered",
            "--max-queue-wait-seconds", "1800"
        });

        Assert.Equal("/cli/input", options.InputRoot);
        Assert.Equal("/cli/output.xlsx", options.OutputPath);
        Assert.Equal(new[] { 8, 10 }, options.PoolIds);
        Assert.Equal(new[] { "schedule", "triggered" }, options.ExcludeReasons);
        Assert.Equal(1800d, options.MaxQueueWaitSeconds);
    }
}
