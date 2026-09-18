using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Query;

public sealed class DetailPolicyEvaluatorTests
{
    [Fact]
    public void ListOnly_never_needs_detail()
        => Assert.False(DetailPolicyEvaluator.NeedsDetail(TestRuns.Create(definitionId: null, status: null), DetailPolicy.ListOnly));

    [Fact]
    public void FillMissing_complete_run_needs_no_detail()
    {
        var run = TestRuns.Create(
            queueTime: TestRuns.FetchedAt,
            startTime: TestRuns.FetchedAt,
            finishTime: TestRuns.FetchedAt);

        Assert.False(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Theory]
    [InlineData("definitionId")]
    [InlineData("definitionName")]
    [InlineData("status")]
    [InlineData("result")]
    public void FillMissing_missing_contract_field_needs_detail(string field)
    {
        var run = field switch
        {
            "definitionId" => TestRuns.Create(definitionId: null),
            "definitionName" => TestRuns.Create(definitionName: null),
            "status" => TestRuns.Create(status: null),
            _ => TestRuns.Create(result: null)
        };

        Assert.True(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Theory]
    [InlineData("definitionName")]
    [InlineData("status")]
    [InlineData("result")]
    public void FillMissing_blank_strings_count_as_missing(string field)
    {
        var run = field switch
        {
            "definitionName" => TestRuns.Create(definitionName: "   "),
            "status" => TestRuns.Create(status: ""),
            _ => TestRuns.Create(result: "\t")
        };

        Assert.True(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Fact]
    public void FillMissing_completed_run_missing_a_timestamp_needs_detail()
    {
        var run = TestRuns.Create(
            queueTime: TestRuns.FetchedAt,
            startTime: TestRuns.FetchedAt,
            finishTime: null);

        Assert.True(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Fact]
    public void FillMissing_completed_comparison_is_case_insensitive()
    {
        var run = TestRuns.Create(
            status: "COMPLETED",
            queueTime: null,
            startTime: TestRuns.FetchedAt,
            finishTime: TestRuns.FetchedAt);

        Assert.True(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Fact]
    public void FillMissing_non_completed_run_missing_timestamps_needs_no_detail()
    {
        var run = TestRuns.Create(
            status: "inProgress",
            queueTime: null,
            startTime: null,
            finishTime: null);

        Assert.False(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }

    [Fact]
    public void Optional_missing_fields_never_trigger_detail()
    {
        var run = TestRuns.Create(
            buildNumber: null,
            reason: null,
            poolId: null,
            poolName: null,
            sourceBranch: null,
            queueTime: TestRuns.FetchedAt,
            startTime: TestRuns.FetchedAt,
            finishTime: TestRuns.FetchedAt);

        Assert.False(DetailPolicyEvaluator.NeedsDetail(run, DetailPolicy.FillMissing));
    }
}
