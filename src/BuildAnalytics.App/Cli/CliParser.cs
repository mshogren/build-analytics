using System.Globalization;
using BuildAnalytics.App.Reporting;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Pure argument parser for the single combined action (ADR-98/111). Config values are passed
/// in as data; this type performs no IO. There are no verbs, and legacy flags (including the
/// old <c>retrieve</c>/<c>report</c> verbs) are unknown options and therefore usage errors.
/// No process-exiting here.
/// </summary>
public static class CliParser
{
    public const string DefaultApiVersion = "7.1";

    public static CliParseResult Parse(string[] args, BuildAnalyticsConfig? config = null)
    {
        if (args is null || args.Length == 0 || (args.Length > 0 && IsHelp(args[0])))
        {
            return CliParseResult.Help();
        }

        try
        {
            return CliParseResult.ForRun(ParseCliOptions(args, config));
        }
        catch (CliUsageException exception)
        {
            return CliParseResult.UsageError(exception.Message);
        }
    }

    private static CliOptions ParseCliOptions(string[] args, BuildAnalyticsConfig? config)
    {
        string? organization = null;
        string? project = null;
        string? outputRoot = null;
        string? outputPath = null;
        string? apiVersion = null;
        var detailPolicy = (DetailPolicy?)null;
        int? maxRuns = null;
        var quiet = false;

        for (var index = 0; index < args.Length; index++)
        {
            var (name, inline) = Split(args[index]);
            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                if (index + 1 >= args.Length)
                {
                    throw new CliUsageException($"Missing value for '{name}'.");
                }

                return args[++index];
            }

            switch (name)
            {
                case "--org":
                    organization = Value();
                    break;
                case "--project":
                    project = Value();
                    break;
                case "--output-root":
                    outputRoot = Value();
                    break;
                case "--out":
                    outputPath = Value();
                    break;
                case "--detail":
                    detailPolicy = ParseDetailPolicy(Value());
                    break;
                case "--max-runs":
                    maxRuns = ParseNonNegative(name, Value());
                    break;
                case "--api-version":
                    apiVersion = Value();
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--config":
                    _ = Value();
                    break;
                default:
                    throw new CliUsageException($"Unknown option '{name}'.");
            }
        }

        // Precedence: CLI -> config -> built-in default (ADR-98).
        var effectiveOrganization = organization ?? config?.Organization;
        var effectiveProject = project ?? config?.Project;
        var effectiveOutputRoot = outputRoot ?? config?.OutputRoot;

        Require(effectiveOrganization, "--org");
        Require(effectiveProject, "--project");
        Require(effectiveOutputRoot, "--output-root");

        var effectiveDetail = detailPolicy
            ?? (config?.Detail is { } configuredDetail ? ParseDetailPolicy(configuredDetail) : DetailPolicy.ListOnly);
        var effectiveOutputPath = Coalesce(
            outputPath,
            config?.Out,
            Path.Combine(effectiveOutputRoot!, ExcelTimingReportWriter.DefaultFileName));

        return new CliOptions(
            effectiveOrganization!,
            effectiveProject!,
            effectiveOutputRoot!,
            effectiveOutputPath,
            effectiveDetail,
            maxRuns ?? config?.MaxRuns ?? int.MaxValue,
            Coalesce(apiVersion, config?.ApiVersion, DefaultApiVersion),
            quiet || (config?.Quiet ?? false));
    }

    private static bool IsHelp(string value)
        => value is "help" or "--help" or "-h";

    private static (string Name, string? Inline) Split(string token)
    {
        var separator = token.IndexOf('=', StringComparison.Ordinal);
        return separator < 0
            ? (token, null)
            : (token[..separator], token[(separator + 1)..]);
    }

    private static void Require(string? value, string flag)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException($"'{flag}' is required.");
        }
    }

    private static string Coalesce(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static DetailPolicy ParseDetailPolicy(string value)
        => value.ToLowerInvariant() switch
        {
            "list" or "listonly" or "list-only" => DetailPolicy.ListOnly,
            "fill-missing" or "fillmissing" or "fill" => DetailPolicy.FillMissing,
            _ => throw new CliUsageException($"Invalid --detail value '{value}'. Expected 'list' or 'fill-missing'.")
        };

    private static int ParseNonNegative(string flag, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new CliUsageException($"Invalid {flag} value '{value}'. Expected a non-negative integer.");

    private sealed class CliUsageException(string message) : Exception(message);
}
