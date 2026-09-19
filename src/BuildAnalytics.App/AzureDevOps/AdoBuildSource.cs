using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Ports;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Azure DevOps build source: one paged build-list GET per call (ADR-55) and an opt-in detail
/// fallback. Holds the per-session run budget; the pipeline owns paging, repeated-token
/// detection, and restart.
/// </summary>
public sealed class AdoBuildSource : IBuildSource, IDisposable
{
    /// <summary>Fixed <c>$top</c> for a full page (ADR-99).</summary>
    internal const int PageSize = 1000;

    private readonly HttpClient _httpClient;
    private readonly AdoRequestExecutor _executor;
    private readonly int _maxRuns;
    private readonly TimeProvider _timeProvider;

    private int _fetched;

    public AdoBuildSource(
        HttpMessageHandler httpMessageHandler,
        TimeProvider timeProvider,
        IDelayScheduler delayScheduler,
        int maxRuns = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(httpMessageHandler);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = new HttpClient(httpMessageHandler, disposeHandler: false);
        _executor = new AdoRequestExecutor(_httpClient, delayScheduler, timeProvider);
        _timeProvider = timeProvider;
        _maxRuns = maxRuns;
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/>; the injected handler stays caller-owned.</summary>
    public void Dispose() => _httpClient.Dispose();

    public async Task<BuildPage> ListAsync(BuildQuery query, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (continuationToken is null)
        {
            _fetched = 0;
        }

        var remaining = _maxRuns - _fetched;
        var top = AdoPageBudget.TrimTop(PageSize, remaining)
            ?? throw new RetrievalStoppedException(StopReason.RunCapReached, remainingBudget: remaining);

        var path = AdoUrlBuilder.ListPath(query.Project);
        var uri = AdoUrlBuilder.ListUri(query, top, continuationToken);

        var response = await _executor
            .SendAsync(() => CreateRequest(uri), path, AdoRequestKind.List, runId: null, continuationToken, cancellationToken)
            .ConfigureAwait(false);

        using var document = ParseBody(response.Body, path);
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

        _fetched += runs.Count;

        // ADR-96: the ADO `count` field is the total matching the query; it drives progress.
        var totalCount = AdoJsonMapper.GetInt(document.RootElement, "count");
        if (totalCount is < 0)
        {
            totalCount = null;
        }

        return new BuildPage(runs, response.ContinuationToken, totalCount);
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

        using var document = ParseBody(response.Body, path);
        var run = AdoJsonMapper.MapBuild(document.RootElement, RunSource.Detail, _timeProvider.GetUtcNow());

        if (run.Id <= 0 || run.Id != runId)
        {
            // ADR-86/90: an id mismatch is a protocol anomaly, not a 404.
            throw new InvalidDetailPayloadException(runId, run.Id);
        }

        return run;
    }

    private static JsonDocument ParseBody(string body, string requestPath)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            // ADR-84: classify a malformed body instead of letting it escape.
            throw new AdoRequestException(requestPath, exception);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
