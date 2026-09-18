using System.Globalization;
using BuildAnalytics.App.Reporting;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Pure argument parser for <c>retrieve</c> / <c>report</c> / <c>help</c> (ADR-79..81).
/// Legacy flags (--config, --pat, --input-root, --count-only, ...) are unknown options and
/// therefore usage errors. No process-exiting here.
/// </summary>
public static class CliParser
{
    public const string DefaultApiVersion = "7.1";
    public const int DefaultPageSize = 1000;

    public static CliParseResult Parse(string[] args)
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
                "retrieve" => ParseRetrieve(args[1..]),
                "report" => ParseReport(args[1..]),
                _ => CliParseResult.UsageError($"Unknown command '{verb}'. Expected 'retrieve', 'report', or 'help'.")
            };
        }
        catch (CliUsageException exception)
        {
            return CliParseResult.UsageError(exception.Message);
        }
    }

    private static CliParseResult ParseRetrieve(string[] args)
    {
        string? organization = null;
        string? project = null;
        string? outputRoot = null;
        string? apiVersion = null;
        DateTimeOffset? minTime = null;
        DateTimeOffset? maxTime = null;
        var definitionIds = new List<int>();
        var definitionGlobs = new List<string>();
        var detailPolicy = DetailPolicy.ListOnly;
        var maxRuns = int.MaxValue;
        var pageSize = DefaultPageSize;
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
                case "--from":
                    minTime = ParseInstant(name, Value());
                    break;
                case "--to":
                    maxTime = ParseInstant(name, Value());
                    break;
                case "--definition-id":
                    definitionIds.AddRange(ParseIds(Value()));
                    break;
                case "--definition":
                    definitionGlobs.Add(Value());
                    break;
                case "--detail":
                    detailPolicy = ParseDetailPolicy(Value());
                    break;
                case "--max-runs":
                    maxRuns = ParseNonNegative(name, Value());
                    break;
                case "--page-size":
                    pageSize = ParsePositive(name, Value());
                    break;
                case "--api-version":
                    apiVersion = Value();
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    throw new CliUsageException($"Unknown option '{name}'.");
            }
        }

        Require(organization, "--org");
        Require(project, "--project");
        Require(outputRoot, "--output-root");

        return CliParseResult.ForRetrieve(new RetrieveOptions(
            organization!,
            project!,
            outputRoot!,
            minTime,
            maxTime,
            definitionIds,
            definitionGlobs,
            detailPolicy,
            maxRuns,
            pageSize,
            string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion!,
            quiet));
    }

    private static CliParseResult ParseReport(string[] args)
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
                default:
                    throw new CliUsageException($"Unknown option '{name}'.");
            }
        }

        Require(outputRoot, "--output-root");

        return CliParseResult.ForReport(new ReportOptions(
            outputRoot!,
            string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(outputRoot!, ExcelTimingReportWriter.DefaultFileName)
                : outputPath!,
            quiet));
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

    private static DetailPolicy ParseDetailPolicy(string value)
        => value.ToLowerInvariant() switch
        {
            "list" or "listonly" or "list-only" => DetailPolicy.ListOnly,
            "fill-missing" or "fillmissing" or "fill" => DetailPolicy.FillMissing,
            _ => throw new CliUsageException($"Invalid --detail value '{value}'. Expected 'list' or 'fill-missing'.")
        };

    private static DateTimeOffset ParseInstant(string flag, string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new CliUsageException($"Invalid {flag} value '{value}'. Expected an ISO-8601 timestamp.");

    private static int ParseNonNegative(string flag, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new CliUsageException($"Invalid {flag} value '{value}'. Expected a non-negative integer.");

    private static int ParsePositive(string flag, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? parsed
            : throw new CliUsageException($"Invalid {flag} value '{value}'. Expected a positive integer.");

    private static IEnumerable<int> ParseIds(string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new CliUsageException($"Invalid --definition-id value '{part}'. Expected a positive integer.");
            }

            yield return id;
        }
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
