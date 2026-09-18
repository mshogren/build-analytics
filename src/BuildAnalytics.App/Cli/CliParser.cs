using System.Globalization;
using BuildAnalytics.App.Reporting;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Pure argument parser for <c>retrieve</c> / <c>report</c> / <c>help</c> (ADR-79..81, ADR-98).
/// Config values are passed in as data; this type performs no IO. Legacy flags are unknown
/// options and therefore usage errors. No process-exiting here.
/// </summary>
public static class CliParser
{
    public const string DefaultApiVersion = "7.1";

    public static CliParseResult Parse(string[] args, BuildAnalyticsConfig? config = null)
    {
        if (args is null || args.Length == 0)
        {
            return CliParseResult.Help();
        }

        var verb = args[0];
        if (IsHelp(verb))
        {
            return CliParseResult.Help();
        }

        try
        {
            return verb switch
            {
                "retrieve" => ParseRetrieve(args[1..], config),
                "report" => ParseReport(args[1..], config),
                _ => CliParseResult.UsageError($"Unknown command '{verb}'. Expected 'retrieve', 'report', or 'help'.")
            };
        }
        catch (CliUsageException exception)
        {
            return CliParseResult.UsageError(exception.Message);
        }
    }

    private static CliParseResult ParseRetrieve(string[] args, BuildAnalyticsConfig? config)
    {
        string? organization = null;
        string? project = null;
        string? outputRoot = null;
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
        var effectiveApiVersion = Coalesce(apiVersion, config?.ApiVersion, DefaultApiVersion);
        var effectiveMaxRuns = maxRuns ?? config?.MaxRuns ?? int.MaxValue;
        var effectiveQuiet = quiet || (config?.Quiet ?? false);

        return CliParseResult.ForRetrieve(new RetrieveOptions(
            effectiveOrganization!,
            effectiveProject!,
            effectiveOutputRoot!,
            effectiveDetail,
            effectiveMaxRuns,
            effectiveApiVersion,
            effectiveQuiet));
    }

    private static CliParseResult ParseReport(string[] args, BuildAnalyticsConfig? config)
    {
        string? outputRoot = null;
        string? outputPath = null;
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
                case "--output-root":
                    outputRoot = Value();
                    break;
                case "--out":
                    outputPath = Value();
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

        var effectiveOutputRoot = outputRoot ?? config?.OutputRoot;
        Require(effectiveOutputRoot, "--output-root");

        var effectiveOutputPath = Coalesce(
            outputPath,
            config?.Out,
            Path.Combine(effectiveOutputRoot!, ExcelTimingReportWriter.DefaultFileName));
        var effectiveQuiet = quiet || (config?.Quiet ?? false);

        return CliParseResult.ForReport(new ReportOptions(effectiveOutputRoot!, effectiveOutputPath, effectiveQuiet));
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
