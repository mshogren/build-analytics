using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.App.Reporting;
using BuildAnalytics.App.Retrieval;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.Cli;

/// <summary>
/// Composition root + runner for the CLI (ADR-79..82). Parsing is pure; this maps the parse
/// result to an exit code and wires the adapters. Retrieval composes network adapters; reporting
/// composes read-only stores only (no HttpClient / IBuildSource).
/// </summary>
public sealed class CliApplication
{
    public const string Usage =
        """
        build-analytics retrieve --org <url> --project <name> --output-root <path>
            [--from <iso>] [--to <iso>] [--definition-id <id>]... [--definition <glob>]...
            [--detail list|fill-missing] [--max-runs <n>] [--page-size <n>] [--api-version <v>] [--quiet]
        build-analytics report --output-root <path> [--out <file.xlsx>] [--quiet]
        build-analytics help
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

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CliParser.Parse(args);

        if (parsed.IsError)
        {
            _console.WriteError(parsed.Error!);
            _console.WriteError(Usage);
            return 2;
        }

        return parsed.Verb switch
        {
            CliVerb.Help => RunHelp(),
            CliVerb.Retrieve => await RunRetrieveAsync(parsed.Retrieve!, cancellationToken).ConfigureAwait(false),
            CliVerb.Report => await RunReportAsync(parsed.Report!, cancellationToken).ConfigureAwait(false),
            _ => 2
        };
    }

    private int RunHelp()
    {
        _console.WriteLine(Usage);
        return 0;
    }

    private async Task<int> RunRetrieveAsync(RetrieveOptions options, CancellationToken cancellationToken)
    {
        var pat = _credentials.GetPat();
        if (string.IsNullOrEmpty(pat))
        {
            _console.WriteError($"{EnvironmentCredentialProvider.VariableName} is required for retrieve.");
            return 1;
        }

        using var authHandler = new PatAuthHandler(pat) { InnerHandler = _handlers.Create() };

        using var source = new AdoBuildSource(authHandler, _clock, _delay, new AdoBuildSourceOptions
        {
            PageSize = options.PageSize,
            MaxRuns = options.MaxRuns
        });

        var runStore = new FileRunStore(options.OutputRoot, new PhysicalFileOperations());
        using var manifests = new FileManifestStore(options.OutputRoot, new PhysicalFileOperations());
        var pipeline = new RetrievalPipeline(source, source, runStore, manifests, _clock);

        var query = new BuildQuery(
            options.Organization,
            options.Project,
            options.MinTime,
            options.MaxTime,
            [],
            options.DetailPolicy,
            options.ApiVersion)
        {
            DefinitionIds = options.DefinitionIds,
            DefinitionNames = options.DefinitionGlobs
        };

        try
        {
            var result = await pipeline.RunAsync(query, cancellationToken).ConfigureAwait(false);

            if (result.Status == ManifestStatus.Completed)
            {
                if (!options.Quiet)
                {
                    _console.WriteError($"Retrieval completed: {result.RunsWritten} run(s), {result.PagesFetched} page(s).");
                }

                return 0;
            }

            _console.WriteError(Describe(result));
            return 1;
        }
        catch (FingerprintMismatchException exception)
        {
            _console.WriteError(exception.Message);
            return 1;
        }
        catch (OutputRootInUseException exception)
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

    private async Task<int> RunReportAsync(ReportOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using var manifestReader = FileManifestStore.OpenReadOnly(options.OutputRoot);
            var runStore = new FileRunStore(options.OutputRoot, new PhysicalFileOperations());
            var writer = new ExcelTimingReportWriter(options.OutputPath);
            var service = new ReportingService(manifestReader, runStore, writer);

            var result = await service.GenerateAsync(cancellationToken).ConfigureAwait(false);

            _console.WriteLine(options.OutputPath);
            if (!options.Quiet)
            {
                _console.WriteError($"Report complete: {result.RunsRead} run(s) read, {result.CorruptSkipped} corrupt skipped.");
            }

            return 0;
        }
        catch (ReportingErrorException exception)
        {
            _console.WriteError(exception.Message);
            return 1;
        }
        catch (UnsupportedSchemaVersionException exception)
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

    /// <summary>ADR-62: surfaces the structured pause fields, never the lastError text.</summary>
    private static string Describe(RetrievalResult result)
    {
        var status = result.Status switch
        {
            ManifestStatus.Paused => "paused",
            ManifestStatus.Failed => "failed",
            _ => result.Status.ToString().ToLowerInvariant()
        };

        var details = new List<string>();
        if (result.Pause is { } pause)
        {
            details.Add($"reason={pause}");
        }

        if (result.RetryAfter is { } retryAfter)
        {
            details.Add($"retryAfter={retryAfter}");
        }

        if (result.RemainingBudget is { } remainingBudget)
        {
            details.Add($"remainingBudget={remainingBudget}");
        }

        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
        return $"Retrieval {status}{suffix}.";
    }
}
