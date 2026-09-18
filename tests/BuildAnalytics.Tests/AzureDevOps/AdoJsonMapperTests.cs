using System.Text.Json;
using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoJsonMapperTests
{
    private const string FullBuildJson = """
        {
          "id": 42,
          "buildNumber": "20240101.1",
          "status": "completed",
          "result": "succeeded",
          "queueTime": "2024-01-01T00:00:00Z",
          "startTime": "2024-01-01T00:01:00Z",
          "finishTime": "2024-01-01T00:02:00Z",
          "reason": "manual",
          "sourceBranch": "refs/heads/main",
          "definition": { "id": 7, "name": "ci", "path": "\\folder" },
          "queue": { "id": 3, "name": "Default", "pool": { "id": 9, "name": "Pool A" } },
          "requestedFor": { "displayName": "someone" },
          "requestedBy": { "displayName": "other" },
          "sourceVersion": "abc123",
          "tags": [ "nightly" ],
          "uri": "vstfs:///Build/Build/42",
          "webUrl": "https://dev.azure.com/org/project/_build/results?buildId=42",
          "keepForever": true
        }
        """;

    [Fact]
    public void Maps_only_contract_fields_and_flattens_nested_objects()
    {
        using var document = JsonDocument.Parse(FullBuildJson);
        var fetchedAt = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var run = AdoJsonMapper.MapBuild(document.RootElement, RunSource.List, fetchedAt);

        Assert.Equal(1, run.SchemaVersion);
        Assert.Equal(RunSource.List, run.Source);
        Assert.Equal(fetchedAt, run.FetchedAt);
        Assert.Equal(42, run.Id);
        Assert.Equal(7, run.DefinitionId);
        Assert.Equal("ci", run.DefinitionName);
        Assert.Equal("20240101.1", run.BuildNumber);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), run.QueueTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero), run.StartTime);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 2, 0, TimeSpan.Zero), run.FinishTime);
        Assert.Equal("completed", run.Status);
        Assert.Equal("succeeded", run.Result);
        Assert.Equal("manual", run.Reason);
        Assert.Equal(9, run.PoolId);
        Assert.Equal("Pool A", run.PoolName);
        Assert.Equal("refs/heads/main", run.SourceBranch);
    }

    [Fact]
    public void Missing_fields_map_to_null()
    {
        using var document = JsonDocument.Parse("""{ "id": 1 }""");

        var run = AdoJsonMapper.MapBuild(document.RootElement, RunSource.Detail, TestRuns.FetchedAt);

        Assert.Equal(1, run.Id);
        Assert.Null(run.DefinitionId);
        Assert.Null(run.DefinitionName);
        Assert.Null(run.BuildNumber);
        Assert.Null(run.QueueTime);
        Assert.Null(run.StartTime);
        Assert.Null(run.FinishTime);
        Assert.Null(run.Status);
        Assert.Null(run.Result);
        Assert.Null(run.Reason);
        Assert.Null(run.PoolId);
        Assert.Null(run.PoolName);
        Assert.Null(run.SourceBranch);
    }

    [Fact]
    public void ExtractItems_reads_the_value_wrapper_and_bare_arrays()
    {
        using var wrapped = JsonDocument.Parse("""{ "count": 1, "value": [ { "id": 1 }, { "id": 2 } ] }""");
        using var bare = JsonDocument.Parse("""[ { "id": 1 } ]""");

        Assert.Equal(2, AdoJsonMapper.ExtractItems(wrapped.RootElement).Count());
        Assert.Single(AdoJsonMapper.ExtractItems(bare.RootElement));
    }
}
