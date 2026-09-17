sealed record BuildAnalyticsRunRow(
    int? RunId,
    int? DefinitionId,
    string? DefinitionName,
    string? BuildNumber,
    string? Status,
    string? Result,
    string? Reason,
    string? QueueTime,
    string? StartTime,
    string? FinishTime,
    double? QueueWaitSeconds,
    double? RunDurationSeconds,
    double? TotalDurationSeconds,
    string? SourceBranch,
    string? SourceVersion,
    string? RequestedFor,
    string? RequestedBy,
    string? QueueName,
    int? PoolId,
    string? PoolName,
    bool? KeepForever,
    string? Tags,
    string? Uri,
    string? WebUrl,
    string SourcePath)
{
    public static readonly string[] Headers =
    [
        "RunId", "DefinitionId", "DefinitionName", "BuildNumber", "Status", "Result", "Reason",
        "QueueTime", "StartTime", "FinishTime", "QueueWaitSeconds", "RunDurationSeconds", "TotalDurationSeconds",
        "SourceBranch", "SourceVersion", "RequestedFor", "RequestedBy", "QueueName", "PoolId", "PoolName",
        "KeepForever", "Tags", "Uri", "WebUrl", "SourcePath"
    ];

    public object?[] ToCells() =>
    [
        RunId,
        DefinitionId,
        DefinitionName,
        BuildNumber,
        Status,
        Result,
        Reason,
        QueueTime,
        StartTime,
        FinishTime,
        QueueWaitSeconds,
        RunDurationSeconds,
        TotalDurationSeconds,
        SourceBranch,
        SourceVersion,
        RequestedFor,
        RequestedBy,
        QueueName,
        PoolId,
        PoolName,
        KeepForever,
        Tags,
        Uri,
        WebUrl,
        SourcePath
    ];
}
