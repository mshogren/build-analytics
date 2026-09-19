using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.App.Reporting;
using BuildAnalytics.App.Retrieval;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Composition root + runner for the single combined command (ADR-79..82, ADR-110, ADR-111).
/// Parsing is pure; this maps the parse result to an exit code and wires the adapters.
/// </summary>
public sealed class CliApplication
{
    public const string Usage =
        """
        build-analytics --org <url> --project <name> --output-root <path>
            [--out <file.xlsx>] [--detail list|fill-missing] [--max-runs <n>] [--api-version <v>] [--quiet] [--config <path>]
        build-analytics --help
        """;

    private readonly ICredentialProvider _credentials;
    private readonly IConsoleOutput _console;
    private readonly IHttpMessageHandlerFactory _handlers;
    private readonly TimeProvider _clock;
    private readonly IDelayScheduler _delay;

    public CliApplication(
        ICredentialProvider credentials,
        IConsoleOutput console,
        IHttpMessageHandlerFactory handlers,
        TimeProvider? clock = null,
        IDelayScheduler? delay = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(handlers);

        _credentials = credentials;
        _console = console;
        _handlers = handlers;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? new SystemDelayScheduler();
    }

    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        => RunAsync(args, config: null, cancellationToken);

    public async Task<int> RunAsync(string[] args, BuildAnalyticsConfig? config, CancellationToken cancellationToken)
    {
        var parsed = CliParser.Parse(args, config);

        if (parsed.IsError)
        {
            _console.WriteError(parsed.Error!);
            _console.WriteError(Usage);
            return 2;
        }

        if (parsed.IsHelp)
        {
            _console.WriteLine(Usage);
            return 0;
        }

        return await RunAsync(parsed.Options!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var pat = _credentials.GetPat();
        if (string.IsNullOrEmpty(pat))
        {
            _console.WriteError($"{EnvironmentCredentialProvider.VariableName} is required.");
            return 1;
        }

        try
        {
            // ADR-110: stateless run - clear the previous log and report first.
            OutputCleaner.Clear(options.OutputRoot, options.OutputPath);

            using var authHandler = new PatAuthHandler(pat) { InnerHandler = _handlers.Create() };
            using var source = new AdoBuildSource(authHandler, _clock, _delay, options.MaxRuns);

            var runStore = new FileRunStore(options.OutputRoot, new PhysicalFileOperations());
            IRetrievalProgress progress = options.Quiet
                ? NullRetrievalProgress.Instance
                : new ConsoleRetrievalProgress(_console);

            var query = new BuildQuery(
                options.Organization,
                options.Project,
                options.DetailPolicy,
                options.ApiVersion);

            var pipeline = new RetrievalPipeline(source, runStore, progress);
            var retrieval = await pipeline.RunAsync(query, cancellationToken).ConfigureAwait(false);

            progress.GeneratingReport();
            var writer = new ExcelTimingReportWriter(options.OutputPath);
            var reporting = new ReportingService(runStore, writer);
            var report = await reporting.GenerateAsync(cancellationToken).ConfigureAwait(false);

            _console.WriteLine(options.OutputPath);
            if (!options.Quiet)
            {
                _console.WriteError($"Report complete: {report.RunsRead} run(s) read, {report.CorruptSkipped} corrupt skipped, {retrieval.FailedRunIds.Count} skipped (detail unavailable).");
            }

            return 0;
        }
        catch (RetrievalStoppedException exception)
        {
            _console.WriteError(Describe(exception));
            return 1;
        }
        catch (ReportingWriteException exception)
        {
            _console.WriteError(exception.Message);
            return 1;
        }
        catch (StorageException exception)
        {
            _console.WriteError(exception.Message);
            return 1;
        }
    }

    /// <summary>ADR-62: surfaces the structured stop fields, never a persisted-text style.</summary>
    private static string Describe(RetrievalStoppedException exception)
    {
        var details = new List<string> { $"reason={exception.Reason}" };
        if (exception.RetryAfter is { } retryAfter)
        {
            details.Add($"retryAfter={retryAfter}");
        }

        if (exception.RemainingBudget is { } remainingBudget)
        {
            details.Add($"remainingBudget={remainingBudget}");
        }

        return $"Retrieval stopped ({string.Join(", ", details)}).";
    }
}
