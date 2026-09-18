namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Adapter-local retrieval budget. Neither value is part of the query fingerprint:
/// <see cref="PageSize"/> shapes the wire request and <see cref="MaxRuns"/> is a
/// runtime budget, not identity.
/// </summary>
public sealed record AdoBuildSourceOptions
{
    public const int DefaultPageSize = 1000;

    /// <summary>Requested <c>$top</c> for a full page.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>
    /// Total run budget for one adapter session. <c>0</c> pauses before the first call;
    /// a negative value is rejected. Use <see cref="int.MaxValue"/> for unbounded retrieval.
    /// </summary>
    public int MaxRuns { get; init; } = int.MaxValue;
}
