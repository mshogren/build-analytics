using System.Text.Json;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Loads <c>build-analytics.config.json</c> (ADR-98). This is the only place the config file is
/// read; <see cref="CliParser"/> stays pure. Unknown keys are ignored; a <c>pat</c> key is a
/// hard usage error naming <c>AZDO_PAT</c> and is never read.
/// </summary>
public static class ConfigLoader
{
    public const string DefaultFileName = "build-analytics.config.json";

    /// <summary>
    /// Resolves and reads the config for the given CLI arguments. A missing default file is not an
    /// error; a missing file named via <c>--config</c> is. No file is read unless it is requested.
    /// </summary>
    public static ConfigLoadResult Load(string[] args)
    {
        var (explicitPath, explicitGiven, scanError) = FindExplicitPath(args);
        if (scanError is not null)
        {
            return new ConfigLoadResult(null, scanError);
        }

        string path;
        if (explicitGiven)
        {
            if (string.IsNullOrWhiteSpace(explicitPath))
            {
                return new ConfigLoadResult(null, "Missing value for '--config'.");
            }

            path = explicitPath!;
            if (!File.Exists(path))
            {
                return new ConfigLoadResult(null, $"Config file '{path}' was not found.");
            }
        }
        else
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), DefaultFileName);
            if (!File.Exists(path))
            {
                return new ConfigLoadResult(null, null);
            }
        }

        return Read(path);
    }

    private static ConfigLoadResult Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult(null, $"Config file '{Path.GetFileName(path)}' could not be read.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return new ConfigLoadResult(null, $"Config file '{Path.GetFileName(path)}' is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ConfigLoadResult(null, $"Config file '{Path.GetFileName(path)}' must contain a JSON object.");
            }

            if (TryGet(root, "pat", out _))
            {
                return new ConfigLoadResult(null, "Config key 'pat' is not allowed; set the PAT in the AZDO_PAT environment variable instead.");
            }

            string? organization = null;
            string? project = null;
            string? outputRoot = null;
            string? apiVersion = null;
            string? output = null;
            int? maxRuns = null;
            bool? quiet = null;

            if (TryGet(root, "org", out var orgValue))
            {
                if (!TryString(orgValue, "org", out organization, out var error))
                {
                    return new ConfigLoadResult(null, error);
                }
            }

            if (TryGet(root, "project", out var projectValue))
            {
                if (!TryString(projectValue, "project", out project, out var error))
                {
                    return new ConfigLoadResult(null, error);
                }
            }

            if (TryGet(root, "outputRoot", out var outputRootValue))
            {
                if (!TryString(outputRootValue, "outputRoot", out outputRoot, out var error))
                {
                    return new ConfigLoadResult(null, error);
                }
            }

            if (TryGet(root, "apiVersion", out var apiVersionValue))
            {
                if (!TryString(apiVersionValue, "apiVersion", out apiVersion, out var error))
                {
                    return new ConfigLoadResult(null, error);
                }
            }

            if (TryGet(root, "out", out var outValue))
            {
                if (!TryString(outValue, "out", out output, out var error))
                {
                    return new ConfigLoadResult(null, error);
                }
            }

            if (TryGet(root, "maxRuns", out var maxRunsValue))
            {
                if (maxRunsValue.ValueKind != JsonValueKind.Number || !maxRunsValue.TryGetInt32(out var parsed))
                {
                    return new ConfigLoadResult(null, "Config key 'maxRuns' must be an integer.");
                }

                if (parsed < 0)
                {
                    return new ConfigLoadResult(null, "Config key 'maxRuns' must be a non-negative integer.");
                }

                maxRuns = parsed;
            }

            if (TryGet(root, "quiet", out var quietValue))
            {
                if (quietValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return new ConfigLoadResult(null, "Config key 'quiet' must be a boolean.");
                }

                quiet = quietValue.GetBoolean();
            }

            return new ConfigLoadResult(
                new BuildAnalyticsConfig(organization, project, outputRoot, apiVersion, maxRuns, quiet, output),
                null);
        }
    }

    private static bool TryString(JsonElement value, string key, out string? parsed, out string? error)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            parsed = value.GetString();
            error = null;
            return true;
        }

        parsed = null;
        error = $"Config key '{key}' must be a string.";
        return false;
    }

    private static (string? Path, bool Given, string? Error) FindExplicitPath(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var separator = args[index].IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? args[index] : args[index][..separator];
            if (!string.Equals(name, "--config", StringComparison.Ordinal))
            {
                continue;
            }

            if (separator >= 0)
            {
                return (args[index][(separator + 1)..], true, null);
            }

            if (index + 1 >= args.Length)
            {
                return (null, true, "Missing value for '--config'.");
            }

            return (args[index + 1], true, null);
        }

        return (null, false, null);
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
