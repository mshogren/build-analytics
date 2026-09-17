using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed class BuildAnalyticsExportOptions
{
    public string OrganizationUrl { get; init; } = Environment.GetEnvironmentVariable("AZDO_ORG_URL") ?? string.Empty;
    public string Project { get; init; } = Environment.GetEnvironmentVariable("AZDO_PROJECT") ?? string.Empty;
    public string Pat { get; init; } = Environment.GetEnvironmentVariable("AZDO_PAT") ?? string.Empty;
    public string OutputRoot { get; init; } = Path.Combine(Environment.CurrentDirectory, $"build-analytics-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    public List<int> DefinitionIds { get; init; } = new();
    public List<string> DefinitionNames { get; init; } = new();
    public DateTimeOffset? MinTime { get; init; }
    public DateTimeOffset? MaxTime { get; init; }
    public int ThrottleLimit { get; init; } = 6;
    public int MaxRuns { get; init; }
    public bool CountOnly { get; init; }

    public static BuildAnalyticsExportOptions Parse(string[] args)
    {
        var configPath = GetConfigPath(args);
        var config = BuildAnalyticsConfig.Load(configPath);

        var orgUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AZDO_ORG_URL"),
            config.OrganizationUrl);

        var project = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AZDO_PROJECT"),
            config.Project);

        var pat = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AZDO_PAT"),
            config.Pat);

        var outputRoot = FirstNonEmpty(
            Environment.GetEnvironmentVariable("BUILD_ANALYTICS_OUTPUT_ROOT"),
            config.OutputRoot,
            Path.Combine(Environment.CurrentDirectory, $"build-analytics-{DateTime.UtcNow:yyyyMMdd-HHmmss}"));

        var definitionIds = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_DEFINITION_IDS") is { Length: > 0 } definitionIdsEnv
            ? ParseInts(definitionIdsEnv)
            : (config.DefinitionIds?.ToList() ?? new List<int>());

        var definitionNames = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_DEFINITION_NAMES") is { Length: > 0 } definitionNamesEnv
            ? ParseStrings(definitionNamesEnv)
            : (config.DefinitionNames?.ToList() ?? new List<string>());

        DateTimeOffset? minTime = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_MIN_TIME") is { Length: > 0 } minTimeEnv
            ? DateTimeOffset.Parse(minTimeEnv, CultureInfo.InvariantCulture)
            : config.MinTime;

        DateTimeOffset? maxTime = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_MAX_TIME") is { Length: > 0 } maxTimeEnv
            ? DateTimeOffset.Parse(maxTimeEnv, CultureInfo.InvariantCulture)
            : config.MaxTime;

        var throttleLimit = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_THROTTLE_LIMIT") is { Length: > 0 } throttleEnv
            ? int.Parse(throttleEnv, CultureInfo.InvariantCulture)
            : config.ThrottleLimit ?? 6;

        var maxRuns = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_MAX_RUNS") is { Length: > 0 } maxRunsEnv
            ? int.Parse(maxRunsEnv, CultureInfo.InvariantCulture)
            : config.MaxRuns ?? 0;

        var countOnly = Environment.GetEnvironmentVariable("BUILD_ANALYTICS_COUNT_ONLY") is { Length: > 0 } countOnlyEnv
            ? bool.Parse(countOnlyEnv)
            : config.CountOnly ?? false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            switch (arg)
            {
                case "--config":
                case "-config":
                    configPath = NextValue();
                    break;
                case "--org-url":
                case "-org-url":
                case "--organization-url":
                case "-organization-url":
                    orgUrl = NextValue();
                    break;
                case "--project":
                case "-project":
                    project = NextValue();
                    break;
                case "--pat":
                case "-pat":
                    pat = NextValue();
                    break;
                case "--output-root":
                case "-output-root":
                    outputRoot = NextValue();
                    break;
                case "--definition-id":
                case "-definition-id":
                    definitionIds.AddRange(ParseInts(NextValue()));
                    break;
                case "--definition-name":
                case "-definition-name":
                    definitionNames.AddRange(ParseStrings(NextValue()));
                    break;
                case "--min-time":
                case "-min-time":
                    minTime = DateTimeOffset.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--max-time":
                case "-max-time":
                    maxTime = DateTimeOffset.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--throttle-limit":
                case "-throttle-limit":
                    throttleLimit = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--max-runs":
                case "-max-runs":
                    maxRuns = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--count-only":
                    countOnly = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)) configPath = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--org-url=", StringComparison.OrdinalIgnoreCase)) orgUrl = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) project = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--pat=", StringComparison.OrdinalIgnoreCase)) pat = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--output-root=", StringComparison.OrdinalIgnoreCase)) outputRoot = arg.Split('=', 2)[1];
                    else if (arg.StartsWith("--definition-id=", StringComparison.OrdinalIgnoreCase)) definitionIds.AddRange(ParseInts(arg.Split('=', 2)[1]));
                    else if (arg.StartsWith("--definition-name=", StringComparison.OrdinalIgnoreCase)) definitionNames.AddRange(ParseStrings(arg.Split('=', 2)[1]));
                    else if (arg.StartsWith("--min-time=", StringComparison.OrdinalIgnoreCase)) minTime = DateTimeOffset.Parse(arg.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("--max-time=", StringComparison.OrdinalIgnoreCase)) maxTime = DateTimeOffset.Parse(arg.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("--throttle-limit=", StringComparison.OrdinalIgnoreCase)) throttleLimit = int.Parse(arg.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    else if (arg.StartsWith("--max-runs=", StringComparison.OrdinalIgnoreCase)) maxRuns = int.Parse(arg.Split('=', 2)[1], CultureInfo.InvariantCulture);
                    else if (arg.Equals("--count-only", StringComparison.OrdinalIgnoreCase)) countOnly = true;
                    else if (arg.StartsWith("-")) throw new ArgumentException($"Unknown argument: {arg}");
                    break;
            }
        }

        return new BuildAnalyticsExportOptions
        {
            OrganizationUrl = Require(orgUrl, "AZDO_ORG_URL / --org-url"),
            Project = Require(project, "AZDO_PROJECT / --project"),
            Pat = Require(pat, "AZDO_PAT / --pat"),
            OutputRoot = outputRoot,
            DefinitionIds = definitionIds,
            DefinitionNames = definitionNames,
            MinTime = minTime,
            MaxTime = maxTime,
            ThrottleLimit = throttleLimit <= 0 ? 6 : throttleLimit,
            MaxRuns = Math.Max(0, maxRuns),
            CountOnly = countOnly
        };

        static List<int> ParseInts(string value)
        {
            var items = new List<int>();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) items.Add(parsed);
            }
            return items;
        }

        static List<string> ParseStrings(string value)
            => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        static void PrintHelp()
        {
            Console.WriteLine("ADO build-analytics export");
            Console.WriteLine("--config <path> --org-url <url> --project <name> --pat <pat>");
            Console.WriteLine("--output-root <path> --definition-id <ids> --definition-name <names>");
            Console.WriteLine("--min-time <o> --max-time <o> --throttle-limit <n> --max-runs <n>");
            Console.WriteLine("--count-only");
        }

        static string? FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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

        static string Require(string? value, string name)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
            throw new ArgumentException($"{name} is required.");
        }
    }
}

sealed class BuildAnalyticsExporter
{
    private readonly BuildAnalyticsExportOptions _options;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public BuildAnalyticsExporter(BuildAnalyticsExportOptions options)
    {
        _options = options;
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.Pat}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var org = _options.OrganizationUrl.TrimEnd('/');
        var project = _options.Project.Trim('/');
        var rawRoot = Path.Combine(_options.OutputRoot, "runs");
        Directory.CreateDirectory(rawRoot);

        var definitionIds = await ResolveDefinitionIdsAsync(org, project, cancellationToken);
        var runs = await GetRunsAsync(org, project, definitionIds, cancellationToken);
        Console.WriteLine($"Found {runs.Count} runs");

        await File.WriteAllTextAsync(Path.Combine(_options.OutputRoot, "runs.json"), JsonSerializer.Serialize(runs, _jsonOptions), cancellationToken);

        if (_options.CountOnly)
        {
            Console.WriteLine($"Total runs: {runs.Count}");
            return;
        }

        var totalRuns = runs.Count;
        var processedCount = 0;
        var savedCount = 0;
        var skippedCount = 0;

        Console.WriteLine($"Exporting runs: 0/{totalRuns}");

        await Parallel.ForEachAsync(runs, new ParallelOptions { MaxDegreeOfParallelism = _options.ThrottleLimit, CancellationToken = cancellationToken }, async (run, ct) =>
        {
            var saved = await SaveRunAsync(org, project, rawRoot, run, ct);
            if (saved) Interlocked.Increment(ref savedCount);
            else Interlocked.Increment(ref skippedCount);

            var current = Interlocked.Increment(ref processedCount);
            var percentage = totalRuns == 0 ? 0 : (current * 100) / totalRuns;
            lock (this)
            {
                Console.WriteLine($"Exporting runs: {current}/{totalRuns} ({percentage}%)");
            }
        });

        Console.WriteLine($"Saved {savedCount} runs, skipped {skippedCount} existing runs");
        Console.WriteLine($"Done. Output: {_options.OutputRoot}");
    }

    private async Task<List<BuildAnalyticsRunStub>> GetRunsAsync(string org, string project, IReadOnlyCollection<int> definitionIds, CancellationToken ct)
    {
        var runs = new List<BuildAnalyticsRunStub>();
        Console.WriteLine("Fetching runs: 0");
        var query = new Dictionary<string, string>
        {
            ["api-version"] = "7.1",
            ["$top"] = "1000",
            ["queryOrder"] = "queueTimeDescending"
        };
        if (_options.MinTime is not null) query["minTime"] = _options.MinTime.Value.ToString("o");
        if (_options.MaxTime is not null) query["maxTime"] = _options.MaxTime.Value.ToString("o");
        if (definitionIds.Count > 0) query["definitions"] = string.Join(',', definitionIds);

        string? continuation = null;
        do
        {
            var response = await SendAsync(org, $"{project}/_apis/build/builds", query, continuation, ct);
            using var doc = JsonDocument.Parse(response.Json);
            var root = doc.RootElement;
            var items = ExtractItems(root);

            foreach (var item in items)
            {
                var id = TryGetInt(item, "id") ?? 0;
                if (id <= 0) continue;
                runs.Add(new BuildAnalyticsRunStub(id, GetString(item, "buildNumber"), GetNestedString(item, "definition", "name")));
                if (_options.MaxRuns > 0 && runs.Count >= _options.MaxRuns)
                {
                    Console.WriteLine($"Fetching runs: {runs.Count}");
                    return runs;
                }
            }

            Console.WriteLine($"Fetching runs: {runs.Count}");
            continuation = response.ContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuation));

        return runs;
    }

    private async Task<IReadOnlyCollection<int>> ResolveDefinitionIdsAsync(string org, string project, CancellationToken ct)
    {
        var resolved = new HashSet<int>();
        foreach (var id in _options.DefinitionIds)
        {
            if (id > 0) resolved.Add(id);
        }

        if (_options.DefinitionNames.Count == 0) return resolved.ToArray();

        var defs = new List<JsonElement>();
        string? continuation = null;
        do
        {
            var response = await SendAsync(org, $"{project}/_apis/build/definitions", new Dictionary<string, string>
            {
                ["api-version"] = "7.1",
                ["$top"] = "1000"
            }, continuation, ct);

            using var doc = JsonDocument.Parse(response.Json);
            defs.AddRange(ExtractItems(doc.RootElement).Select(x => x.Clone()));
            continuation = response.ContinuationToken;
        } while (!string.IsNullOrWhiteSpace(continuation));

        foreach (var patternText in _options.DefinitionNames)
        {
            var regex = new Regex(WildcardToRegex(patternText), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var def in defs)
            {
                var name = GetString(def, "name");
                var path = GetString(def, "path");
                if ((name is not null && regex.IsMatch(name)) || (path is not null && regex.IsMatch(path)))
                {
                    var id = TryGetInt(def, "id");
                    if (id is > 0) resolved.Add(id.Value);
                }
            }
        }

        return resolved.ToArray();
    }

    private async Task<bool> SaveRunAsync(string org, string project, string rawRoot, BuildAnalyticsRunStub run, CancellationToken ct)
    {
        var folderName = $"{run.Id}__{SafeName(run.DefinitionName ?? run.BuildNumber ?? "Unknown")}";
        var runFolder = Path.Combine(rawRoot, folderName);
        var runJsonPath = Path.Combine(runFolder, "run.json");

        try
        {
            if (File.Exists(runJsonPath) && new FileInfo(runJsonPath).Length > 0)
            {
                return false;
            }

            Directory.CreateDirectory(runFolder);

            var detail = await SendAsync(org, $"{project}/_apis/build/builds/{run.Id}", new Dictionary<string, string>
            {
                ["api-version"] = "7.1"
            }, null, ct);

            await File.WriteAllTextAsync(runJsonPath, detail.Json, ct);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Run {run.Id} failed: {ex.Message}");
            return false;
        }
    }

    private async Task<(string Json, string? ContinuationToken)> SendAsync(string org, string path, IReadOnlyDictionary<string, string> query, string? continuationToken, CancellationToken ct)
    {
        var uri = BuildUri(org, path, query, continuationToken);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        resp.Headers.TryGetValues("x-ms-continuationtoken", out var values);
        return (body, values is null ? null : string.Join(',', values));
    }

    private static Uri BuildUri(string org, string path, IReadOnlyDictionary<string, string> query, string? continuationToken)
    {
        var sb = new StringBuilder();
        foreach (var kvp in query)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kvp.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kvp.Value));
        }
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString("continuationToken"));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(continuationToken));
        }

        var url = $"{org.TrimEnd('/')}/{path.TrimStart('/')}";
        if (sb.Length > 0) url += $"?{sb}";
        return new Uri(url, UriKind.Absolute);
    }

    private static IEnumerable<JsonElement> ExtractItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
        }
    }

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return GetString(current);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return GetString(prop);
    }

    private static string? GetString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.ToString();

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return TryGetInt(prop);
    }

    private static int? TryGetInt(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i) ? i : element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;

    private static string SafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
    }

    private static string WildcardToRegex(string pattern)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                '.' or '$' or '^' or '{' or '[' or '(' or '|' or ')' or '+' or '\\' => $"\\{ch}",
                _ => ch.ToString()
            });
        }
        sb.Append('$');
        return sb.ToString();
    }
}

sealed record BuildAnalyticsRunStub(int Id, string? BuildNumber, string? DefinitionName);
