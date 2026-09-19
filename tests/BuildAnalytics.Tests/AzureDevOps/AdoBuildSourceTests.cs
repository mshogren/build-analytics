using System.Net;
using System.Net.Http;
using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoBuildSourceTests
{
    private static readonly DateTimeOffset ClockNow = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_maps_runs_stamps_fetchedAt_and_returns_the_token()
    {
        var (source, handler, _, clock) = Create();
        clock.UtcNow = ClockNow;
        handler.EnqueueJson(
            """
            {
              "count": 2,
              "value": [
                { "id": 1, "definition": { "id": 5, "name": "ci" }, "status": "completed", "result": "succeeded" },
                { "id": 2, "definition": { "id": 5, "name": "ci" }, "queue": { "pool": { "id": 9, "name": "Pool" } } }
              ]
            }
            """,
            continuationToken: "page-2");

        var page = await source.ListAsync(Query(), continuationToken: null, default);

        Assert.Equal(2, page.Runs.Count);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal("page-2", page.ContinuationToken);
        Assert.All(page.Runs, run =>
        {
            Assert.Equal(RunSource.List, run.Source);
            Assert.Equal(ClockNow, run.FetchedAt);
        });
        Assert.Equal(9, page.Runs[1].PoolId);
        Assert.Equal("Pool", page.Runs[1].PoolName);
    }

    [Fact]
    public async Task List_skips_items_without_a_positive_id()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueJson("""{ "value": [ { "buildNumber": "x" }, { "id": 7 } ] }""");

        var page = await source.ListAsync(Query(), null, default);

        Assert.Single(page.Runs);
        Assert.Equal(7, page.Runs[0].Id);
    }

    [Fact]
    public async Task Paging_forwards_the_continuation_token()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueJson("""{ "value": [] }""", continuationToken: "t1");
        handler.EnqueueJson("""{ "value": [] }""");

        await source.ListAsync(Query(), null, default);
        await source.ListAsync(Query(), "t1", default);

        Assert.Contains("continuationToken=t1", handler.RequestUris[1].Query, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task MaxRuns_trims_top_to_the_remaining_budget()
    {
        var (source, handler, _, _) = Create(maxRuns: 3);
        handler.EnqueueJson("""{ "value": [ { "id": 1 } ] }""", continuationToken: "next");

        await source.ListAsync(Query(), null, default);

        Assert.Contains("$top=3", handler.RequestUris[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exhausted_budget_stops_without_a_call()
    {
        var (source, handler, _, _) = Create(maxRuns: 1);
        handler.EnqueueJson("""{ "value": [ { "id": 1 } ] }""", continuationToken: "next");

        await source.ListAsync(Query(), null, default);
        var countAfterFirstPage = handler.RequestCount;

        var exception = await Assert.ThrowsAsync<RetrievalStoppedException>(
            () => source.ListAsync(Query(), "next", default));

        Assert.Equal(StopReason.RunCapReached, exception.Reason);
        Assert.Equal(countAfterFirstPage, handler.RequestCount);
    }

    [Fact]
    public async Task Zero_maxRuns_stops_before_the_first_call()
    {
        var (source, handler, _, _) = Create(maxRuns: 0);

        var exception = await Assert.ThrowsAsync<RetrievalStoppedException>(
            () => source.ListAsync(Query(), null, default));

        Assert.Equal(StopReason.RunCapReached, exception.Reason);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Negative_maxRuns_is_rejected()
    {
        var (source, _, _, _) = Create(maxRuns: -1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => source.ListAsync(Query(), null, default));
    }

    [Fact]
    public async Task BadRequest_on_a_continuation_page_is_invalid_token()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<InvalidContinuationTokenException>(
            () => source.ListAsync(Query(), "stale", default));
    }

    [Fact]
    public async Task Detail_stamps_source_and_targets_the_run()
    {
        var (source, handler, _, clock) = Create();
        clock.UtcNow = ClockNow;
        handler.EnqueueJson("""{ "id": 42, "definition": { "id": 5, "name": "ci" }, "status": "completed", "result": "succeeded" }""");

        var run = await source.GetDetailAsync(Query(), 42, default);

        Assert.Equal(RunSource.Detail, run.Source);
        Assert.Equal(ClockNow, run.FetchedAt);
        Assert.Equal("/org/project/_apis/build/builds/42", handler.RequestUris[0].AbsolutePath);
    }

    [Fact]
    public async Task Detail_404_is_RunNotFoundException()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<RunNotFoundException>(() => source.GetDetailAsync(Query(), 42, default));
    }

    [Fact]
    public async Task Detail_rejects_a_non_positive_run_id()
    {
        var (source, _, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetDetailAsync(Query(), 0, default));
    }

    [Fact]
    public async Task Malformed_list_body_is_wrapped_as_AdoRequestException()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueJson("{ not json");

        await Assert.ThrowsAsync<AdoRequestException>(() => source.ListAsync(Query(), null, default));
    }

    [Fact]
    public async Task Detail_id_mismatch_throws_InvalidDetailPayloadException()
    {
        var (source, handler, _, _) = Create();
        handler.EnqueueJson("""{ "id": 6, "definition": { "id": 5, "name": "ci" } }""");

        var exception = await Assert.ThrowsAsync<InvalidDetailPayloadException>(
            () => source.GetDetailAsync(Query(), 5, default));

        Assert.Equal(5, exception.RequestedRunId);
        Assert.Equal(6, exception.ReturnedRunId);
    }

    private static BuildQuery Query()
        => new(
            "https://dev.azure.com/org",
            "project",
            DetailPolicy.FillMissing,
            "7.1");

    private static (AdoBuildSource Source, ScriptedHttpMessageHandler Handler, FakeDelayScheduler Delays, FakeTimeProvider Clock) Create(
        int maxRuns = int.MaxValue)
    {
        var handler = new ScriptedHttpMessageHandler();
        var delays = new FakeDelayScheduler();
        var clock = new FakeTimeProvider { UtcNow = ClockNow };
        var source = new AdoBuildSource(handler, clock, delays, maxRuns);
        return (source, handler, delays, clock);
    }
}
