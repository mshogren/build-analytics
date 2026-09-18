namespace BuildAnalytics.App.AzureDevOps;

/// <summary>
/// Adapter-local retrieval budget. <see cref="MaxRuns"/> is a runtime budget, not identity.
/// Page size is a fixed internal constant (ADR-99).
/// </summary>
public sealed record AdoBuildSourceOptions
{
    /// <summary>
    /// Total run budget for one adapter session. <c>0</c> pauses before the first call;
    /// a negative value is rejected. Use <see cref="int.MaxValue"/> for unbounded retrieval.
    /// </summary>
    public int MaxRuns { get; init; } = int.MaxValue;
}
