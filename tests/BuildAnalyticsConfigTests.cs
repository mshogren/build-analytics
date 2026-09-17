using System.Text.Json;
using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsConfigTests
{
    [Fact]
    public void Load_ReadsJson_WithCommentsAndTrailingCommas()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "build-analytics.config.json");
        File.WriteAllText(path, """
        {
          // comment
          "organizationUrl": "https://example",
          "project": "AGLC",
          "pat": "secret",
          "definitionIds": [1, 2,],
          "definitionNames": ["A*",],
          "poolIds": [9,],
          "excludeReasons": ["schedule",],
          "maxQueueWaitSeconds": 7200,
        }
        """);

        var config = BuildAnalyticsConfig.Load(path);

        Assert.Equal("https://example", config.OrganizationUrl);
        Assert.Equal("AGLC", config.Project);
        Assert.Equal("secret", config.Pat);
        Assert.Equal(new[] { 1, 2 }, config.DefinitionIds);
        Assert.Equal(new[] { "A*" }, config.DefinitionNames);
        Assert.Equal(new[] { 9 }, config.PoolIds);
        Assert.Equal(new[] { "schedule" }, config.ExcludeReasons);
        Assert.Equal(7200d, config.MaxQueueWaitSeconds);
    }

    [Fact]
    public void Load_MissingPath_ReturnsEmptyConfig()
    {
        var config = BuildAnalyticsConfig.Load(null);

        Assert.Null(config.OrganizationUrl);
        Assert.Null(config.Project);
        Assert.Null(config.Pat);
        Assert.Null(config.OutputRoot);
    }
}
