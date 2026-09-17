using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;

sealed class BuildAnalyticsSpreadsheetOptions
{
    public string InputRoot { get; init; } = "/workspace/build-analytics-full";
    public string OutputPath { get; init; } = "/workspace/build-analytics-full.xlsx";
    public int[] PoolIds { get; init; } = [];
    public string[] ExcludeReasons { get; init; } = [];
    public double? MaxQueueWaitSeconds { get; init; }

    public static BuildAnalyticsSpreadsheetOptions Parse(string[] args)
    {
        var configPath = GetConfigPath(args);
        var config = BuildAnalyticsConfig.Load(configPath);

        var inputRoot = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BUILD_ANALYTICS_INPUT_ROOT"),
            config.InputRoot,
            "/workspace/build-analytics-full");

        var outputPath = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BUILD_ANALYTICS_OUTPUT_PATH"),
            config.OutputPath,
            "/workspace/build-analytics-full.xlsx");

        var poolIds = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_POOL_IDS") is { Length: > 0 } poolIdsEnv
            ? ParseInts(poolIdsEnv)
            : (config.PoolIds?.ToList() ?? new List<int>());

        var excludeReasons = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_EXCLUDE_REASONS") is { Length: > 0 } excludeReasonsEnv
            ? ParseStrings(excludeReasonsEnv)
            : (config.ExcludeReasons?.ToList() ?? new List<string>());

        double? maxQueueWaitSeconds = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_MAX_QUEUE_WAIT_SECONDS") is { Length: > 0 } maxQueueEnv
            ? double.Parse(maxQueueEnv, CultureInfo.InvariantCulture)
            : config.MaxQueueWaitSeconds;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            switch (arg)
            {
                case "--config":
                case "-config":
                    configPath = Next();
                    break;
                case "--input-root":
                case "-input-root":
                    inputRoot = Next();
                    break;
                case "--output":
                case "-output":
                case "--output-path":
                case "-output-path":
                    outputPath = Next();
                    break;
                case "--pool-id":
                case "-pool-id":
                    poolIds = ParseInts(Next());
                    break;
                case "--exclude-reason":
                case "-exclude-reason":
                    excludeReasons = ParseStrings(Next());
                    break;
                case "--max-queue-wait-seconds":
                case "-max-queue-wait-seconds":
                    maxQueueWaitSeconds = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                case "-?":
                    Console.WriteLine("Usage: spreadsheet --input-root <path> --output <file.xlsx> [--pool-id <ids>] [--exclude-reason <reason>] [--max-queue-wait-seconds <sec>]");
                    Environment.Exit(0);
                    break;
                default:
                    if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)) configPath = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--input-root=", StringComparison.OrdinalIgnoreCase)) inputRoot = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--output=", StringComparison.OrdinalIgnoreCase)) outputPath = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--output-path=", StringComparison.OrdinalIgnoreCase)) outputPath = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--pool-id=", StringComparison.OrdinalIgnoreCase)) poolIds = ParseInts(arg.Split('=', 2)[1]);
                    else if (arg.StartsWith("--exclude-reason=", StringComparison.OrdinalIgnoreCase)) excludeReasons = ParseStrings(arg.Split('=', 2)[1]);
                    else if (arg.StartsWith("--max-queue-wait-seconds=", StringComparison.OrdinalIgnoreCase)) maxQueueWaitSeconds = double.Parse(arg.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("-")) throw new ArgumentException($"Unknown argument: {arg}");
                    break;
            }
        }

        return new BuildAnalyticsSpreadsheetOptions
        {
            InputRoot = inputRoot,
            OutputPath = outputPath,
            PoolIds = poolIds.ToArray(),
            ExcludeReasons = excludeReasons.ToArray(),
            MaxQueueWaitSeconds = maxQueueWaitSeconds
        };

        static List<int> ParseInts(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
        static List<string> ParseStrings(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        static string GetConfigPath(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase) || arg.Equals("-config", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return args[i + 1];
                }
                else if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Split('=', 2)[1];
                }
            }

            var env = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return BuildAnalyticsConfig.DefaultPath;
        }
    }
}

sealed class BuildAnalyticsSpreadsheetExporter
{
    private readonly BuildAnalyticsSpreadsheetOptions _options;

    public BuildAnalyticsSpreadsheetExporter(BuildAnalyticsSpreadsheetOptions options)
    {
        _options = options;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_options.InputRoot))
        {
            throw new DirectoryNotFoundException($"Input root not found: {_options.InputRoot}");
        }

        var rows = await LoadRunsAsync(_options.InputRoot, cancellationToken);
        var filtered = ApplyFilters(rows);
        Console.WriteLine($"Loaded {rows.Count} runs from {_options.InputRoot}");
        Console.WriteLine($"Filtered to {filtered.Count} runs");

        var wb = new XLWorkbook();
        WriteOverviewSheet(wb, filtered);
        WriteRunsSheet(wb, filtered);
        WriteMonthlySheet(wb, filtered);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath)) ?? Directory.GetCurrentDirectory());
        wb.SaveAs(_options.OutputPath);
        Console.WriteLine($"Wrote {_options.OutputPath}");
    }

    private IReadOnlyList<BuildAnalyticsRunRow> ApplyFilters(IReadOnlyList<BuildAnalyticsRunRow> rows)
    {
        IEnumerable<BuildAnalyticsRunRow> query = rows;

        if (_options.PoolIds.Length > 0)
        {
            var poolIds = _options.PoolIds.ToHashSet();
            query = query.Where(r => r.PoolId.HasValue && poolIds.Contains(r.PoolId.Value));
        }

        if (_options.ExcludeReasons.Length > 0)
        {
            var excludedReasons = _options.ExcludeReasons.ToHashSet(StringComparer.OrdinalIgnoreCase);
            query = query.Where(r => string.IsNullOrWhiteSpace(r.Reason) || !excludedReasons.Contains(r.Reason));
        }

        if (_options.MaxQueueWaitSeconds is not null)
        {
            query = query.Where(r => !r.QueueWaitSeconds.HasValue || r.QueueWaitSeconds.Value <= _options.MaxQueueWaitSeconds.Value);
        }

        return query.ToList();
    }

    private static async Task<List<BuildAnalyticsRunRow>> LoadRunsAsync(string inputRoot, CancellationToken ct)
    {
        var runsDir = Path.Combine(inputRoot, "runs");
        if (!Directory.Exists(runsDir)) return new List<BuildAnalyticsRunRow>();

        var rows = new List<BuildAnalyticsRunRow>();
        foreach (var runDir in Directory.EnumerateDirectories(runsDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var runJsonPath = Path.Combine(runDir, "run.json");
            if (!File.Exists(runJsonPath)) continue;

            var json = await File.ReadAllTextAsync(runJsonPath, ct);
            using var doc = JsonDocument.Parse(json);
            var run = doc.RootElement;

            rows.Add(new BuildAnalyticsRunRow(
                GetInt(run, "id"),
                GetInt(GetNested(run, "definition"), "id"),
                GetString(GetNested(run, "definition"), "name"),
                GetString(run, "buildNumber"),
                GetString(run, "status"),
                GetString(run, "result"),
                GetString(run, "reason"),
                ToIso(GetString(run, "queueTime")),
                ToIso(GetString(run, "startTime")),
                ToIso(GetString(run, "finishTime")),
                Seconds(GetString(run, "queueTime"), GetString(run, "startTime")),
                Seconds(GetString(run, "startTime"), GetString(run, "finishTime")),
                Seconds(GetString(run, "queueTime"), GetString(run, "finishTime")),
                GetString(run, "sourceBranch"),
                GetString(run, "sourceVersion"),
                GetString(GetNested(run, "requestedFor"), "displayName"),
                GetString(GetNested(run, "requestedBy"), "displayName"),
                GetString(GetNested(run, "queue"), "name"),
                GetInt(GetNested(run, "queue", "pool"), "id"),
                GetString(GetNested(run, "queue", "pool"), "name"),
                GetBool(run, "keepForever"),
                JoinList(GetStringList(run, "tags")),
                GetString(run, "uri"),
                GetString(GetNested(run, "_links", "web"), "href"),
                runDir
            ));
        }

        return rows;
    }

    private static void WriteOverviewSheet(XLWorkbook wb, IReadOnlyList<BuildAnalyticsRunRow> rows)
    {
        var ws = wb.Worksheets.Add("Overview");
        ws.Cell(1, 1).Value = "Metric";
        ws.Cell(1, 2).Value = "Value";

        var total = rows.Count;
        var succeeded = rows.Count(r => string.Equals(r.Result, "succeeded", StringComparison.OrdinalIgnoreCase));
        var failed = rows.Count(r => string.Equals(r.Result, "failed", StringComparison.OrdinalIgnoreCase));
        var partial = rows.Count(r => string.Equals(r.Result, "partiallySucceeded", StringComparison.OrdinalIgnoreCase));
        var canceled = rows.Count(r => string.Equals(r.Result, "canceled", StringComparison.OrdinalIgnoreCase));
        var notStarted = rows.Count(r => string.Equals(r.Status, "notStarted", StringComparison.OrdinalIgnoreCase));
        var completed = rows.Count(r => string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var definitions = rows.Select(r => r.DefinitionName).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var minQueue = rows.Where(r => !string.IsNullOrWhiteSpace(r.QueueTime)).Select(r => r.QueueTime!).OrderBy(x => x).FirstOrDefault();
        var maxFinish = rows.Where(r => !string.IsNullOrWhiteSpace(r.FinishTime)).Select(r => r.FinishTime!).OrderByDescending(x => x).FirstOrDefault();

        var metrics = new (string Metric, object? Value)[]
        {
            ("Runs", total),
            ("Definitions", definitions),
            ("Completed", completed),
            ("Not started", notStarted),
            ("Succeeded", succeeded),
            ("Failed", failed),
            ("Partially succeeded", partial),
            ("Canceled", canceled),
            ("Earliest queueTime", minQueue),
            ("Latest finishTime", maxFinish)
        };

        for (var i = 0; i < metrics.Length; i++)
        {
            SetCellValue(ws.Cell(i + 2, 1), metrics[i].Metric);
            SetCellValue(ws.Cell(i + 2, 2), metrics[i].Value);
        }

        var used = ws.RangeUsed();
        if (used is not null) used.CreateTable();
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static void WriteRunsSheet(XLWorkbook wb, IReadOnlyList<BuildAnalyticsRunRow> rows)
    {
        var ws = wb.Worksheets.Add("Runs");
        var headers = BuildAnalyticsRunRow.Headers;
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var values = rows[r].ToCells();
            for (var c = 0; c < values.Length; c++)
            {
                SetCellValue(ws.Cell(r + 2, c + 1), values[c]);
            }
        }

        var range = ws.Range(1, 1, rows.Count + 1, headers.Length);
        range.CreateTable();
        ws.SheetView.FreezeRows(1);
        ws.SheetView.ZoomScale = 90;
        ws.Columns(1, headers.Length).AdjustToContents();
    }

    private static void WriteMonthlySheet(XLWorkbook wb, IReadOnlyList<BuildAnalyticsRunRow> rows)
    {
        var ws = wb.Worksheets.Add("Monthly");
        ws.Cell(1, 1).Value = "Month";
        ws.Cell(1, 2).Value = "Runs";
        ws.Cell(1, 3).Value = "Succeeded";
        ws.Cell(1, 4).Value = "Failed";
        ws.Cell(1, 5).Value = "Partially Succeeded";
        ws.Cell(1, 6).Value = "Canceled";
        ws.Cell(1, 7).Value = "Not Started";
        ws.Cell(1, 8).Value = "Wait > 5 Min";
        ws.Cell(1, 9).Value = "Avg Queue Wait (sec)";
        ws.Cell(1, 10).Value = "Avg Duration (sec)";
        ws.Cell(1, 11).Value = "Avg Total (sec)";

        var summary = rows
            .GroupBy(r => MonthKey(r.QueueTime))
            .Select(g => new
            {
                Month = g.Key,
                Runs = g.Count(),
                Succeeded = g.Count(x => string.Equals(x.Result, "succeeded", StringComparison.OrdinalIgnoreCase)),
                Failed = g.Count(x => string.Equals(x.Result, "failed", StringComparison.OrdinalIgnoreCase)),
                PartiallySucceeded = g.Count(x => string.Equals(x.Result, "partiallySucceeded", StringComparison.OrdinalIgnoreCase)),
                Canceled = g.Count(x => string.Equals(x.Result, "canceled", StringComparison.OrdinalIgnoreCase)),
                NotStarted = g.Count(x => string.Equals(x.Status, "notStarted", StringComparison.OrdinalIgnoreCase)),
                WaitOverFiveMin = g.Count(x => x.QueueWaitSeconds.HasValue && x.QueueWaitSeconds.Value > 300),
                AvgQueueWait = Average(g.Select(x => x.QueueWaitSeconds)),
                AvgDuration = Average(g.Select(x => x.RunDurationSeconds)),
                AvgTotal = Average(g.Select(x => x.TotalDurationSeconds))
            })
            .OrderBy(x => x.Month, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.Runs)
            .ToList();

        for (var i = 0; i < summary.Count; i++)
        {
            var row = summary[i];
            var r = i + 2;
            SetCellValue(ws.Cell(r, 1), row.Month);
            SetCellValue(ws.Cell(r, 2), row.Runs);
            SetCellValue(ws.Cell(r, 3), row.Succeeded);
            SetCellValue(ws.Cell(r, 4), row.Failed);
            SetCellValue(ws.Cell(r, 5), row.PartiallySucceeded);
            SetCellValue(ws.Cell(r, 6), row.Canceled);
            SetCellValue(ws.Cell(r, 7), row.NotStarted);
            SetCellValue(ws.Cell(r, 8), row.WaitOverFiveMin);
            SetCellValue(ws.Cell(r, 9), row.AvgQueueWait);
            SetCellValue(ws.Cell(r, 10), row.AvgDuration);
            SetCellValue(ws.Cell(r, 11), row.AvgTotal);
        }

        var range = ws.Range(1, 1, summary.Count + 1, 11);
        range.CreateTable();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Value = string.Empty;
                break;
            case int i:
                cell.Value = i;
                break;
            case long l:
                cell.Value = l;
                break;
            case double d:
                cell.Value = d;
                break;
            case decimal m:
                cell.Value = m;
                break;
            case bool b:
                cell.Value = b;
                break;
            case DateTime dt:
                cell.Value = dt;
                break;
            case DateTimeOffset dto:
                cell.Value = dto.DateTime;
                break;
            default:
                cell.Value = value.ToString() ?? string.Empty;
                break;
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var data = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (data.Length == 0) return null;
        return Math.Round(data.Average(), 2);
    }

    private static string MonthKey(string? queueTime)
    {
        if (string.IsNullOrWhiteSpace(queueTime)) return "(unknown)";
        return DateTimeOffset.TryParse(queueTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : "(unknown)";
    }

    private static JsonElement GetNested(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return default;
            current = next;
        }
        return current;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return GetString(prop);
    }

    private static string? GetString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            _ => element.ToString()
        };

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return GetInt(prop);
    }

    private static int? GetInt(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static List<string> GetStringList(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return prop.EnumerateArray().Select(GetString).Where(v => !string.IsNullOrWhiteSpace(v)).ToList()!;
    }

    private static string JoinList(IEnumerable<string> items) => string.Join(';', items);

    private static string? ToIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.ToString("o", CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static double? Seconds(string? start, string? end)
    {
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end)) return null;
        if (DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s) &&
            DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e))
        {
            return Math.Round((e - s).TotalSeconds, 2);
        }
        return null;
    }
}
