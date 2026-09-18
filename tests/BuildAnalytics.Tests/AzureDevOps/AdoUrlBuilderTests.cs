using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoUrlBuilderTests
{
    [Fact]
    public void List_uri_carries_paging_and_query_parameters()
    {
        var uri = AdoUrlBuilder.ListUri(Query(), top: 250, continuationToken: "tok");

        Assert.Equal("https://dev.azure.com", uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/org/project/_apis/build/builds", uri.AbsolutePath);
        Assert.Contains("api-version=7.1", uri.Query, StringComparison.Ordinal);
        Assert.Contains("$top=250", uri.Query, StringComparison.Ordinal);
        Assert.Contains("queryOrder=queueTimeDescending", uri.Query, StringComparison.Ordinal);
        Assert.Contains("continuationToken=tok", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void List_uri_omits_absent_parameters()
    {
        var uri = AdoUrlBuilder.ListUri(Query(), top: 100, continuationToken: null);

        Assert.DoesNotContain("minTime=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("maxTime=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("definitions=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("continuationToken=", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_uri_targets_the_build()
    {
        var uri = AdoUrlBuilder.DetailUri(Query(), runId: 42);

        Assert.Equal("/org/project/_apis/build/builds/42", uri.AbsolutePath);
        Assert.Contains("api-version=7.1", uri.Query, StringComparison.Ordinal);
    }

    private static BuildQuery Query()
        => new("https://dev.azure.com/org", "project", DetailPolicy.FillMissing, "7.1");
}
