using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Azure DevOps build source: paged build-list retrieval, opt-in detail fallback,
/// and client-side definition-name resolution. Holds the per-session run budget; the
/// pipeline is the only caller and drives pages sequentially.
/// </summary>
public sealed class AdoBuildSource : IBuildSource, IDefinitionResolver
{
    private readonly AdoRequestExecutor _executor;
    private readonly AdoBuildSourceOptions _options;
    private readonly TimeProvider _timeProvider;

    private int _fetched;

    public AdoBuildSource(
        HttpClient httpClient,
        IDelayScheduler delayScheduler,
        TimeProvider timeProvider,
        AdoBuildSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _executor = new AdoRequestExecutor(httpClient, delayScheduler, timeProvider);
        _timeProvider = timeProvider;
        _options = options ?? new AdoBuildSourceOptions();
    }

    public async Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (continuationToken is null)
        {
            _fetched = 0;
        }

        var top = AdoPageBudget.TrimTop(_options.PageSize, _options.MaxRuns - _fetched)
            ?? throw new PipelinePausedException(PauseReason.RunCapReached);

        var path = AdoUrlBuilder.ListPath(query.Project);
        var uri = AdoUrlBuilder.ListUri(query, top, continuationToken);

        var response = await _executor
            .SendAsync(() => CreateRequest(uri), path, AdoRequestKind.List, runId: null, continuationToken, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(response.Body);
        var fetchedAt = _timeProvider.GetUtcNow();
        var runs = new List<BuildRun>();

        foreach (var item in AdoJsonMapper.ExtractItems(document.RootElement))
        {
            var run = AdoJsonMapper.MapBuild(item, RunSource.List, fetchedAt);
            if (run.Id > 0)
            {
                runs.Add(run);
            }
        }

        if (response.ContinuationToken is not null && response.ContinuationToken == continuationToken)
        {
            throw new InvalidContinuationTokenException(continuationToken);
        }

        _fetched += runs.Count;
        return new BuildPage(runs, response.ContinuationToken);
    }

    public async Task<BuildRun> GetDetailAsync(BuildQuery query, int runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (runId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runId), runId, "Run id must be positive.");
        }

        var path = AdoUrlBuilder.DetailPath(query.Project, runId);
        var uri = AdoUrlBuilder.DetailUri(query, runId);

        var response = await _executor
            .SendAsync(() => CreateRequest(uri), path, AdoRequestKind.Detail, runId, continuationToken: null, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(response.Body);
        return AdoJsonMapper.MapBuild(document.RootElement, RunSource.Detail, _timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<int>> ResolveAsync(BuildQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resolved = new HashSet<int>((query.DefinitionIds ?? []).Where(id => id > 0));
        var names = (query.DefinitionNames ?? []).Where(name => !string.IsNullOrEmpty(name)).ToArray();

        if (names.Length == 0)
        {
            return Sorted(resolved);
        }

        var definitions = new List<(int Id, string? Name, string? Path)>();
        string? continuationToken = null;

        do
        {
            var path = AdoUrlBuilder.DefinitionsPath(query.Project);
            var uri = AdoUrlBuilder.DefinitionsUri(query, _options.PageSize, continuationToken);

            var response = await _executor
                .SendAsync(() => CreateRequest(uri), path, AdoRequestKind.List, runId: null, continuationToken, cancellationToken)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(response.Body);
            foreach (var item in AdoJsonMapper.ExtractItems(document.RootElement))
            {
                var id = AdoJsonMapper.GetInt(item, "id");
                if (id is > 0)
                {
                    definitions.Add((id.Value, AdoJsonMapper.GetString(item, "name"), AdoJsonMapper.GetString(item, "path")));
                }
            }

            if (response.ContinuationToken is not null && response.ContinuationToken == continuationToken)
            {
                throw new InvalidContinuationTokenException(continuationToken);
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken is not null);

        var patterns = names.Select(AdoWildcard.ToRegex).ToArray();
        foreach (var definition in definitions)
        {
            if (patterns.Any(regex =>
                    (definition.Name is not null && regex.IsMatch(definition.Name))
                    || (definition.Path is not null && regex.IsMatch(definition.Path))))
            {
                resolved.Add(definition.Id);
            }
        }

        return Sorted(resolved);
    }

    private static IReadOnlyList<int> Sorted(HashSet<int> ids)
    {
        var sorted = ids.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
