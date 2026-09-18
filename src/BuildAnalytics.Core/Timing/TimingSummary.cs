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

/// <summary>
/// Overall totals plus the per-month breakdown. <see cref="Months"/> is defensively copied,
/// so callers cannot mutate this value object through the source collection.
/// </summary>
public sealed record TimingSummary
{
    public TimingSummary(TimingTotals overall, IReadOnlyList<MonthlyTimingSummary> months)
    {
        ArgumentNullException.ThrowIfNull(overall);
        ArgumentNullException.ThrowIfNull(months);

        Overall = overall;
        Months = Array.AsReadOnly(months.ToArray());
    }

    public TimingTotals Overall { get; }

    public IReadOnlyList<MonthlyTimingSummary> Months { get; }

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
