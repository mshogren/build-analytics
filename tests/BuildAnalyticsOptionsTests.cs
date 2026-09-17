using Xunit;

[CollectionDefinition("sequential", DisableParallelization = true)]
public sealed class SequentialCollectionDefinition { }

[Collection("sequential")]
public sealed class BuildAnalyticsOptionsTests
{
    [Fact]
    public void ExportOptions_UsesConfig_EnvOverridesConfig_AndCliOverridesEnv()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "build-analytics.config.json");
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

    [Fact]
    public void SpreadsheetOptions_UsesConfig_EnvOverridesConfig_AndCliOverridesEnv()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "build-analytics.config.json");
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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentScope Set(string name, string? value)
        {
            if (!_previous.ContainsKey(name))
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var kvp in _previous)
            {
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
            }
        }
    }
}
