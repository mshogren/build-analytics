using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsClassCoverageTests
{
    [Fact]
    public void BuildAnalyticsConfig_LoadsJson_WithCommentsAndTrailingCommas()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "build-analytics.config.json");
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
    public async Task BuildAnalyticsTool_HelpCommand_WritesHelp()
    {
        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await BuildAnalyticsTool.RunAsync(new[] { "help" });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("build-analytics", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spreadsheet", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAnalyticsRunRow_HeadersMatchCellCount()
    {
        var row = new BuildAnalyticsRunRow(
            42, 7, "Pipe", "2024.01.01.1", "completed", "succeeded", "manual",
            "2024-01-01T00:00:00Z", "2024-01-01T00:05:00Z", "2024-01-01T00:10:00Z",
            300, 300, 600, "refs/heads/main", "abc", "Requester", "Builder",
            "Azure Pipelines", 9, "Azure Pipelines", false, "tag1;tag2",
            "vstfs:///Build/Build/42", "https://example/build/42", "/tmp/run");

        var cells = row.ToCells();

        Assert.Equal(BuildAnalyticsRunRow.Headers.Length, cells.Length);
        Assert.Equal(42, cells[0]);
        Assert.Equal("Pipe", cells[2]);
        Assert.Equal(9, cells[18]);
    }

    [Fact]
    public async Task BuildAnalyticsExporter_ExportsOneRun_UsingConfigFilePath()
    {
        using var temp = new TempDirectory();
        using var server = TestHttpServer.Start();
        var outputRoot = Path.Combine(temp.Path, "export");
        var configPath = Path.Combine(temp.Path, "build-analytics.config.json");

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

        await new BuildAnalyticsExporter(options).RunAsync();

        var runPath = Path.Combine(outputRoot, "runs", "101__Pipe A", "run.json");
        Assert.True(File.Exists(runPath));
        Assert.True(File.Exists(Path.Combine(outputRoot, "runs.json")));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(runPath));
        Assert.Equal(101, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Pipe A", doc.RootElement.GetProperty("definition").GetProperty("name").GetString());
    }

    [Fact]
    public async Task BuildAnalyticsSpreadsheetExporter_FiltersAndSummarizesByMonth()
    {
        using var temp = new TempDirectory();
        var inputRoot = Path.Combine(temp.Path, "input");
        var outputPath = Path.Combine(temp.Path, "output.xlsx");
        Directory.CreateDirectory(Path.Combine(inputRoot, "runs"));

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
        var folder = Path.Combine(inputRoot, "runs", $"{id}__{definitionName}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(new
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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

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

    private sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, Func<HttpListenerRequest, string>> _routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Task _loop;

        public Uri BaseUri { get; }

        private TestHttpServer(int port)
        {
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public static TestHttpServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return new TestHttpServer(port);
        }

        public void Register(string path, Func<HttpListenerRequest, string> handler)
        {
            _routes[path] = handler;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                var body = _routes.TryGetValue(path, out var handler)
                    ? handler(ctx.Request)
                    : "{\"error\":\"not-found\"}";
                await WriteJsonAsync(ctx.Response, body, _routes.ContainsKey(path) ? 200 : 404);
            }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string body, int statusCode)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }
        }
    }
}
