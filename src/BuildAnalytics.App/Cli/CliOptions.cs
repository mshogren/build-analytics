namespace BuildAnalytics.App.Cli;

/// <summary>
/// Parsed options for the single combined action (ADR-111): retrieve the whole history, then
/// write the report. Runtime budget defaults to unbounded.
/// </summary>
public sealed record CliOptions(
    string Organization,
    string Project,
    string OutputRoot,
    string OutputPath,
    int MaxRuns,
    string ApiVersion,
    bool Quiet);

/// <summary>
/// Pure parse result. <see cref="CliParser.Parse"/> never exits the process; only Main maps
/// this to an exit code.
/// </summary>
public sealed record CliParseResult(CliOptions? Options, string? Error)
{
    public bool IsError => Error is not null;

    public bool IsHelp => Options is null && Error is null;

    public static CliParseResult Help() => new(null, null);

    public static CliParseResult ForRun(CliOptions options) => new(options, null);

    public static CliParseResult UsageError(string message) => new(null, message);
}
