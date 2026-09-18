namespace BuildAnalytics.Core.Timing;

/// <summary>Aggregated timing metrics for a set of runs. Durations are raw seconds.</summary>
public sealed record TimingTotals(
    int RunCount,
    int SucceededCount,
    int FailedCount,
    int PartiallySucceededCount,
    int CanceledCount,
    int NotStartedCount,
    int WaitOverFiveMinutesCount,
    double? AverageQueueWaitSeconds,
    double? AverageRunDurationSeconds,
    double? AverageTotalDurationSeconds);

/// <summary>Rollup totals for one UTC month, or the unknown bucket.</summary>
public sealed record MonthlyTimingSummary(string Month, TimingTotals Totals);

/// <summary>Overall totals plus the per-month breakdown.</summary>
public sealed record TimingSummary(TimingTotals Overall, IReadOnlyList<MonthlyTimingSummary> Months)
{
    public bool Equals(TimingSummary? other)
        => other is not null && Overall == other.Overall && Months.SequenceEqual(other.Months);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Overall);
        foreach (var month in Months)
        {
            hash.Add(month);
        }

        return hash.ToHashCode();
    }
}
