using BuildAnalytics.App.AzureDevOps;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoPageBudgetTests
{
    [Theory]
    [InlineData(1000, 2500, 1000)]
    [InlineData(1000, 1000, 1000)]
    [InlineData(1000, 5, 5)]
    [InlineData(1000, 1, 1)]
    [InlineData(0, 10, 1)]
    public void TrimTop_is_never_above_page_size_or_below_one(int pageSize, int remaining, int expected)
        => Assert.Equal(expected, AdoPageBudget.TrimTop(pageSize, remaining));

    [Fact]
    public void Zero_remaining_means_no_call()
        => Assert.Null(AdoPageBudget.TrimTop(1000, 0));

    [Fact]
    public void Negative_remaining_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AdoPageBudget.TrimTop(1000, -1));
}
