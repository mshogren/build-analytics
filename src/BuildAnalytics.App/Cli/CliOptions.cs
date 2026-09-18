using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>Top-level command selected by the CLI parser.</summary>
public enum CliVerb
{
    Help,
    Retrieve,
    Report
}

/// <summary>Parsed <c>retrieve</c> options. Runtime budget defaults to unbounded.</summary>
public sealed record RetrieveOptions(
    string Organization,
    string Project,
    string OutputRoot,
    DateTimeOffset? MinTime,
    DateTimeOffset? MaxTime,
    IReadOnlyList<int> DefinitionIds,
    IReadOnlyList<string> DefinitionGlobs,
    DetailPolicy DetailPolicy,
    int MaxRuns,
    int PageSize,
    string ApiVersion,
    bool Quiet);

/// <summary>Parsed <c>report</c> options.</summary>
public sealed record ReportOptions(string OutputRoot, string OutputPath, bool Quiet);

/// <summary>
/// Pure parse result. <see cref="Parse"/> never exits the process; only Main maps this to a code.
/// </summary>
public sealed record CliParseResult(CliVerb Verb, RetrieveOptions? Retrieve, ReportOptions? Report, string? Error)
{
    public bool IsError => Error is not null;

    public static CliParseResult Help() => new(CliVerb.Help, null, null, null);

    public static CliParseResult ForRetrieve(RetrieveOptions options) => new(CliVerb.Retrieve, options, null, null);

    public static CliParseResult ForReport(ReportOptions options) => new(CliVerb.Report, null, options, null);

    public static CliParseResult UsageError(string message) => new(CliVerb.Help, null, null, message);
}
