using BuildAnalytics.App.Cli;

namespace BuildAnalytics.Tests.Cli;

public sealed class ConfigLoaderTests
{
    private static readonly Lock CwdLock = new();

    [Fact]
    public void Missing_default_file_is_not_an_error()
    {
        WithWorkingDirectory(directory =>
        {
            var result = ConfigLoader.Load(["retrieve"]);

            Assert.Null(result.Error);
            Assert.Null(result.Config);
        });
    }

    [Fact]
    public void Default_file_is_loaded_when_present()
    {
        WithWorkingDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, ConfigLoader.DefaultFileName), """{ "org": "o", "project": "p", "outputRoot": "r" }""");

            var result = ConfigLoader.Load(["retrieve"]);

            Assert.Null(result.Error);
            Assert.Equal("o", result.Config!.Organization);
            Assert.Equal("p", result.Config.Project);
            Assert.Equal("r", result.Config.OutputRoot);
        });
    }

    [Fact]
    public void Explicit_missing_file_is_an_error()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ba-config-{Guid.NewGuid():N}.json");

        var result = ConfigLoader.Load(["retrieve", "--config", missing]);

        Assert.NotNull(result.Error);
        Assert.Contains("was not found", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_config_without_a_value_is_an_error()
    {
        var result = ConfigLoader.Load(["retrieve", "--config"]);

        Assert.NotNull(result.Error);
        Assert.Contains("--config", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void All_known_keys_are_loaded_and_unknown_keys_ignored()
    {
        var path = WriteTempConfig(
            """
            {
              "org": "https://dev.azure.com/org",
              "project": "p",
              "outputRoot": "root",
              "apiVersion": "6.0",
              "maxRuns": 25,
              "quiet": true,
              "out": "custom.xlsx",
              "somethingElse": { "nested": true }
            }
            """);

        var result = ConfigLoader.Load(["retrieve", "--config", path]);

        Assert.Null(result.Error);
        var config = result.Config!;
        Assert.Equal("https://dev.azure.com/org", config.Organization);
        Assert.Equal("p", config.Project);
        Assert.Equal("root", config.OutputRoot);
        Assert.Equal("6.0", config.ApiVersion);
        Assert.Equal(25, config.MaxRuns);
        Assert.True(config.Quiet);
        Assert.Equal("custom.xlsx", config.Out);
    }

    [Theory]
    [InlineData("pat")]
    [InlineData("PAT")]
    public void Pat_key_is_a_hard_error_naming_AZDO_PAT(string key)
    {
        var path = WriteTempConfig($$"""{ "{{key}}": "secret" }""");

        var result = ConfigLoader.Load(["retrieve", "--config", path]);

        Assert.NotNull(result.Error);
        Assert.Contains("AZDO_PAT", result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var path = WriteTempConfig("{ not json");

        var result = ConfigLoader.Load(["retrieve", "--config", path]);

        Assert.NotNull(result.Error);
        Assert.Contains("not valid JSON", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrongly_typed_key_is_an_error()
    {
        var path = WriteTempConfig("""{ "maxRuns": "many" }""");

        var result = ConfigLoader.Load(["retrieve", "--config", path]);

        Assert.NotNull(result.Error);
        Assert.Contains("maxRuns", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_maxRuns_is_a_usage_error()
    {
        var path = WriteTempConfig("""{ "maxRuns": -1 }""");

        var result = ConfigLoader.Load(["retrieve", "--config", path]);

        Assert.NotNull(result.Error);
        Assert.Contains("maxRuns", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_equals_form_is_supported()
    {
        var path = WriteTempConfig("""{ "org": "o" }""");

        var result = ConfigLoader.Load(["retrieve", $"--config={path}"]);

        Assert.Null(result.Error);
        Assert.Equal("o", result.Config!.Organization);
    }

    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ba-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void WithWorkingDirectory(Action<string> action)
    {
        lock (CwdLock)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ba-cwd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var original = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(directory);
                action(directory);
            }
            finally
            {
                Directory.SetCurrentDirectory(original);
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
