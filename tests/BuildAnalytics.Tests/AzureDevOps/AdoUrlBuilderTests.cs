using BuildAnalytics.App.AzureDevOps;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoUrlBuilderTests
{
    [Fact]
    public void List_uri_carries_paging_and_query_parameters()
    {
        var query = Query(min: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)) with
        {
            ResolvedDefinitionIds = [3, 1, 1]
        };

        var uri = AdoUrlBuilder.ListUri(query, top: 250, continuationToken: "tok");

        Assert.Equal("https://dev.azure.com", uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/org/project/_apis/build/builds", uri.AbsolutePath);
        Assert.Contains("api-version=7.1", uri.Query, StringComparison.Ordinal);
        Assert.Contains("$top=250", uri.Query, StringComparison.Ordinal);
        Assert.Contains("queryOrder=queueTimeDescending", uri.Query, StringComparison.Ordinal);
        Assert.Contains("minTime=2024-01-01T00%3A00%3A00.0000000%2B00%3A00", uri.Query, StringComparison.Ordinal);
        Assert.Contains("definitions=1%2C3", uri.Query, StringComparison.Ordinal);
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

    [Fact]
    public void Definitions_uri_never_uses_a_name_filter()
    {
        var uri = AdoUrlBuilder.DefinitionsUri(Query(definitionNames: ["ci-*"]), top: 1000, continuationToken: null);

        Assert.Equal("/org/project/_apis/build/definitions", uri.AbsolutePath);
        Assert.Contains("$top=1000", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("name=", uri.Query, StringComparison.Ordinal);
    }

    private static BuildQuery Query(
        DateTimeOffset? min = null,
        DateTimeOffset? max = null,
        IReadOnlyList<string>? definitionNames = null)
        => new(
            "https://dev.azure.com/org",
            "project",
            min,
            max,
            [],
            DetailPolicy.FillMissing,
            "7.1")
        {
            DefinitionNames = definitionNames ?? []
        };
}
