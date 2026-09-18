using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests;

internal static class TestRuns
{
    internal static readonly DateTimeOffset FetchedAt = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static BuildRun Create(
        int id = 1,
        int? definitionId = 1,
        string? definitionName = "ci",
        string? buildNumber = "1",
        DateTimeOffset? queueTime = null,
        DateTimeOffset? startTime = null,
        DateTimeOffset? finishTime = null,
        string? status = "completed",
        string? result = "succeeded",
        string? reason = "manual",
        int? poolId = 1,
        string? poolName = "pool",
        string? sourceBranch = "main",
        RunSource source = RunSource.List)
        => new(
            SchemaVersion: 1,
            Source: source,
            FetchedAt: FetchedAt,
            Id: id,
            DefinitionId: definitionId,
            DefinitionName: definitionName,
            BuildNumber: buildNumber,
            QueueTime: queueTime,
            StartTime: startTime,
            FinishTime: finishTime,
            Status: status,
            Result: result,
            Reason: reason,
            PoolId: poolId,
            PoolName: poolName,
            SourceBranch: sourceBranch);
}
