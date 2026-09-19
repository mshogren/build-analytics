using System.Text.Json;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Serialization;

public sealed class JsonRoundTripTests
{
    [Fact]
    public void BuildRun_JsonRoundTrip_AllFieldsSet()
    {
        var run = TestRuns.Create(
            id: 42,
            definitionId: 7,
            definitionName: "ci",
            buildNumber: "20240101.1",
            queueTime: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            startTime: new DateTimeOffset(2024, 1, 1, 0, 1, 0, TimeSpan.Zero),
            finishTime: new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero),
            status: "completed",
            result: "succeeded",
            reason: "manual",
            poolId: 3,
            poolName: "pool",
            sourceBranch: "refs/heads/main",
            source: RunSource.Detail);

        var back = JsonSerializer.Deserialize<BuildRun>(JsonSerializer.Serialize(run, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.Equal(run, back);
    }

    [Fact]
    public void BuildRun_JsonRoundTrip_OptionalsNull()
    {
        var run = TestRuns.Create(
            definitionId: null,
            definitionName: null,
            buildNumber: null,
            queueTime: null,
            startTime: null,
            finishTime: null,
            status: null,
            result: null,
            reason: null,
            poolId: null,
            poolName: null,
            sourceBranch: null);

        var back = JsonSerializer.Deserialize<BuildRun>(JsonSerializer.Serialize(run, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.Equal(run, back);
    }

    [Fact]
    public void BuildRun_CurrentSchemaVersion_MatchesInstance()
    {
        Assert.Equal(BuildRun.CurrentSchemaVersion, TestRuns.Create().SchemaVersion);
    }

    [Fact]
    public void BuildRun_SerializesFlatDefinitionAndPoolFields()
    {
        var run = TestRuns.Create(definitionId: 7, definitionName: "ci", poolId: 3, poolName: "pool");
        var json = JsonSerializer.Serialize(run, BuildAnalyticsJson.Options);

        Assert.Contains("\"definitionId\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"definitionName\":\"ci\"", json, StringComparison.Ordinal);
        Assert.Contains("\"poolId\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"poolName\":\"pool\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRun_DroppedFields_NeverEmitted()
    {
        var json = JsonSerializer.Serialize(TestRuns.Create(), BuildAnalyticsJson.Options);

        foreach (var dropped in new[] { "\"definition\":", "\"queue\":", "requestedFor", "requestedBy", "sourceVersion", "tags", "uri", "webUrl", "keepForever" })
        {
            Assert.DoesNotContain(dropped, json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(RunSource.List, "list")]
    [InlineData(RunSource.Detail, "detail")]
    public void BuildRun_SerializesSource_AsSnakeCaseString(RunSource source, string expected)
    {
        var json = JsonSerializer.Serialize(TestRuns.Create(source: source), BuildAnalyticsJson.Options);

        Assert.Contains($"\"source\":\"{expected}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRun_DeserializesSource_FromString()
    {
        var json = JsonSerializer.Serialize(TestRuns.Create(source: RunSource.Detail), BuildAnalyticsJson.Options);

        var back = JsonSerializer.Deserialize<BuildRun>(json, BuildAnalyticsJson.Options);

        Assert.Equal(RunSource.Detail, back!.Source);
    }

    [Fact]
    public void BuildRun_GoldenShape_PropertyNames()
    {
        var expected = new[]
        {
            "buildNumber", "definitionId", "definitionName", "fetchedAt", "finishTime", "id",
            "poolId", "poolName", "queueTime", "reason", "result", "schemaVersion", "source",
            "sourceBranch", "startTime", "status"
        };

        Assert.Equal(expected, PropertyNames(TestRuns.Create()));
    }

    [Fact]
    public void BuildRun_Deserialization_IgnoresUnknownFields()
    {
        var json = """
        {
          "schemaVersion": 1,
          "source": "list",
          "fetchedAt": "2024-01-01T00:00:00+00:00",
          "id": 5,
          "definitionId": 2,
          "definitionName": "ci",
          "buildNumber": "1",
          "queueTime": null,
          "startTime": null,
          "finishTime": null,
          "status": "completed",
          "result": "succeeded",
          "reason": "manual",
          "poolId": 1,
          "poolName": "pool",
          "sourceBranch": "main",
          "definition": { "id": 2, "name": "ci" },
          "queue": { "pool": { "id": 1 } },
          "requestedFor": { "displayName": "x" },
          "requestedBy": { "displayName": "y" },
          "sourceVersion": "abc",
          "tags": ["a"],
          "uri": "vstfs:///x",
          "webUrl": "https://example.invalid",
          "keepForever": true
        }
        """;

        var run = JsonSerializer.Deserialize<BuildRun>(json, BuildAnalyticsJson.Options);

        Assert.NotNull(run);
        Assert.Equal(5, run!.Id);
        Assert.Equal(2, run.DefinitionId);
        Assert.Equal("ci", run.DefinitionName);
        Assert.Equal("main", run.SourceBranch);
    }

    [Fact]
    public void BuildRun_NullableTimestamps_RoundTripAsNull()
    {
        var run = TestRuns.Create(queueTime: null, startTime: null, finishTime: null);

        var back = JsonSerializer.Deserialize<BuildRun>(JsonSerializer.Serialize(run, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.Null(back!.QueueTime);
        Assert.Null(back.StartTime);
        Assert.Null(back.FinishTime);
    }

    [Fact]
    public void BuildRun_FetchedAt_RoundTripsAsDateTimeOffset()
    {
        var fetchedAt = new DateTimeOffset(2024, 6, 1, 12, 30, 15, TimeSpan.Zero);
        var run = TestRuns.Create() with { FetchedAt = fetchedAt };

        var back = JsonSerializer.Deserialize<BuildRun>(JsonSerializer.Serialize(run, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.Equal(fetchedAt, back!.FetchedAt);
    }

    [Fact]
    public void BuildPage_JsonRoundTrip_RunsAndContinuationToken()
    {
        var page = new BuildPage([TestRuns.Create(id: 1), TestRuns.Create(id: 2)], "token-1");

        var back = JsonSerializer.Deserialize<BuildPage>(JsonSerializer.Serialize(page, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Runs.Count);
        Assert.Equal("token-1", back.ContinuationToken);
        Assert.Equal(page.Runs, back.Runs);
    }

    [Fact]
    public void BuildPage_EmptyRuns_NullToken()
    {
        var page = new BuildPage([], null);

        var back = JsonSerializer.Deserialize<BuildPage>(JsonSerializer.Serialize(page, BuildAnalyticsJson.Options), BuildAnalyticsJson.Options);

        Assert.Empty(back!.Runs);
        Assert.Null(back.ContinuationToken);
    }

    private static string[] PropertyNames<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, BuildAnalyticsJson.Options);
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
