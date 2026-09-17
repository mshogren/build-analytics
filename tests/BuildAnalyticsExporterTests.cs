using System.Text.Json;
using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsExporterTests
{
    [Fact]
    public async Task RunAsync_ExportsOneRun_UsingConfigFilePath()
    {
        using var temp = new TempDirectory();
        using var server = TestHttpServer.Start();
        var outputRoot = System.IO.Path.Combine(temp.Path, "export");
        var configPath = System.IO.Path.Combine(temp.Path, "build-analytics.config.json");

        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            organizationUrl = server.BaseUri.ToString().TrimEnd('/'),
            project = "AGLC",
            pat = "config-pat",
            outputRoot
        }, new JsonSerializerOptions { WriteIndented = true }));

        server.Register("/AGLC/_apis/build/builds", _ => JsonSerializer.Serialize(new
        {
            count = 1,
            value = new[]
            {
                new
                {
                    id = 101,
                    buildNumber = "2024.01.01.1",
                    definition = new { name = "Pipe A", id = 7 },
                    queueTime = "2024-01-01T00:00:00Z",
                    startTime = "2024-01-01T00:05:00Z",
                    finishTime = "2024-01-01T00:10:00Z",
                    status = "completed",
                    result = "succeeded",
                    reason = "manual",
                    queue = new { pool = new { id = 9, name = "Azure Pipelines" }, name = "Azure Pipelines" },
                    requestedFor = new { displayName = "Requester" },
                    requestedBy = new { displayName = "Builder" },
                    sourceBranch = "refs/heads/main",
                    sourceVersion = "abc123",
                    keepForever = false,
                    tags = new[] { "tag1" },
                    uri = "vstfs:///Build/Build/101",
                    _links = new { web = new { href = "https://example/build/101" } }
                }
            }
        }));

        server.Register("/AGLC/_apis/build/builds/101", _ => JsonSerializer.Serialize(new
        {
            id = 101,
            buildNumber = "2024.01.01.1",
            definition = new { name = "Pipe A", id = 7 },
            queueTime = "2024-01-01T00:00:00Z",
            startTime = "2024-01-01T00:05:00Z",
            finishTime = "2024-01-01T00:10:00Z",
            status = "completed",
            result = "succeeded",
            reason = "manual",
            queue = new { pool = new { id = 9, name = "Azure Pipelines" }, name = "Azure Pipelines" },
            requestedFor = new { displayName = "Requester" },
            requestedBy = new { displayName = "Builder" },
            sourceBranch = "refs/heads/main",
            sourceVersion = "abc123",
            keepForever = false,
            tags = new[] { "tag1" },
            uri = "vstfs:///Build/Build/101",
            _links = new { web = new { href = "https://example/build/101" } }
        }));

        var options = BuildAnalyticsExportOptions.Parse(new[]
        {
            "--config", configPath,
            "--output-root", outputRoot
        });

        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            await new BuildAnalyticsExporter(options).RunAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("Fetching runs: 0", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fetching runs: 1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exporting runs", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1/1", output, StringComparison.OrdinalIgnoreCase);

        var runPath = System.IO.Path.Combine(outputRoot, "runs", "101__Pipe A", "run.json");
        Assert.True(File.Exists(runPath));
        Assert.True(File.Exists(System.IO.Path.Combine(outputRoot, "runs.json")));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(runPath));
        Assert.Equal(101, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Pipe A", doc.RootElement.GetProperty("definition").GetProperty("name").GetString());
    }
}
