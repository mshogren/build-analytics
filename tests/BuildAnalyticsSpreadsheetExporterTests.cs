using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsSpreadsheetExporterTests
{
    [Fact]
    public async Task RunAsync_FiltersAndSummarizesByMonth()
    {
        using var temp = new TempDirectory();
        var inputRoot = System.IO.Path.Combine(temp.Path, "input");
        var outputPath = System.IO.Path.Combine(temp.Path, "output.xlsx");
        Directory.CreateDirectory(System.IO.Path.Combine(inputRoot, "runs"));

        WriteRunJson(inputRoot, 1, "Pipe A", "2024-01-01T00:00:00Z", "2024-01-01T00:02:00Z", "2024-01-01T00:04:00Z", 9, "manual", "succeeded");
        WriteRunJson(inputRoot, 2, "Pipe A", "2024-01-02T00:00:00Z", "2024-01-02T00:10:00Z", "2024-01-02T00:20:00Z", 9, "manual", "failed");
        WriteRunJson(inputRoot, 3, "Pipe A", "2024-02-01T00:00:00Z", "2024-02-01T00:12:00Z", "2024-02-01T00:22:00Z", 9, "manual", "succeeded");
        WriteRunJson(inputRoot, 4, "Pipe A", "2024-02-03T00:00:00Z", "2024-02-03T00:01:00Z", "2024-02-03T00:02:00Z", 9, "schedule", "succeeded");
        WriteRunJson(inputRoot, 5, "Pipe A", "2024-02-04T00:00:00Z", "2024-02-04T00:01:00Z", "2024-02-04T00:02:00Z", 10, "manual", "succeeded");
        WriteRunJson(inputRoot, 6, "Pipe A", "2024-02-05T00:00:00Z", "2024-02-05T02:01:00Z", "2024-02-05T02:02:00Z", 9, "manual", "succeeded");

        var options = BuildAnalyticsSpreadsheetOptions.Parse(new[]
        {
            "--input-root", inputRoot,
            "--output", outputPath,
            "--pool-id", "9",
            "--exclude-reason", "schedule",
            "--max-queue-wait-seconds", "7200"
        });

        await new BuildAnalyticsSpreadsheetExporter(options).RunAsync();

        using var workbook = new XLWorkbook(outputPath);
        var monthly = workbook.Worksheet("Monthly");

        Assert.Equal("Month", monthly.Cell(1, 1).GetString());
        Assert.Equal("Wait > 5 Min", monthly.Cell(1, 8).GetString());
        Assert.Equal("2024-01", monthly.Cell(2, 1).GetString());
        Assert.Equal(2, monthly.Cell(2, 2).GetDouble());
        Assert.Equal(1, monthly.Cell(2, 8).GetDouble());
        Assert.Equal("2024-02", monthly.Cell(3, 1).GetString());
        Assert.Equal(1, monthly.Cell(3, 2).GetDouble());
        Assert.Equal(1, monthly.Cell(3, 8).GetDouble());

        var overview = workbook.Worksheet("Overview");
        Assert.Equal(3, overview.Cell(2, 2).GetDouble());
    }

    private static void WriteRunJson(string inputRoot, int id, string definitionName, string queueTime, string startTime, string finishTime, int poolId, string reason, string result)
    {
        var folder = System.IO.Path.Combine(inputRoot, "runs", $"{id}__{definitionName}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
        {
            id,
            buildNumber = $"2024.01.01.{id}",
            definition = new { id = 7, name = definitionName },
            queueTime,
            startTime,
            finishTime,
            status = "completed",
            result,
            reason,
            queue = new { pool = new { id = poolId, name = "Azure Pipelines" }, name = "Azure Pipelines" },
            requestedFor = new { displayName = "Requester" },
            requestedBy = new { displayName = "Builder" },
            sourceBranch = "refs/heads/main",
            sourceVersion = "abc123",
            keepForever = false,
            tags = new[] { "tag1" },
            uri = $"vstfs:///Build/Build/{id}",
            _links = new { web = new { href = $"https://example/build/{id}" } }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
