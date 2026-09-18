using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Query;

public sealed class BuildQueryFingerprintTests
{
    [Fact]
    public void Identical_effective_queries_produce_the_same_fingerprint()
    {
        Assert.Equal(Fingerprint(Query()), Fingerprint(Query()));
    }

    [Fact]
    public void Fingerprint_is_deterministic_across_calls()
    {
        var query = Query(resolved: [3, 1, 2]);
        Assert.Equal(BuildQueryFingerprint.Compute(query), BuildQueryFingerprint.Compute(query));
    }

    [Fact]
    public void Raw_definition_ids_and_names_do_not_affect_identity()
    {
        var first = Query(resolved: [1, 2], definitionIds: [2, 1], definitionNames: ["a", "b"]);
        var second = Query(resolved: [1, 2], definitionIds: [9], definitionNames: ["z"]);

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Resolved_definition_ids_are_order_and_duplicate_insensitive()
    {
        Assert.Equal(Fingerprint(Query(resolved: [2, 1, 1])), Fingerprint(Query(resolved: [1, 2])));
    }

    [Fact]
    public void Different_resolved_definition_ids_change_identity()
    {
        Assert.NotEqual(Fingerprint(Query(resolved: [1, 2])), Fingerprint(Query(resolved: [1, 3])));
    }

    [Fact]
    public void Null_and_empty_resolved_definition_ids_are_the_same_identity()
    {
        Assert.Equal(Fingerprint(Query(resolved: [])), Fingerprint(Query(resolved: null)));
    }

    [Theory]
    [InlineData(DetailPolicy.ListOnly, DetailPolicy.FillMissing)]
    public void Detail_policy_changes_identity(DetailPolicy first, DetailPolicy second)
    {
        Assert.NotEqual(Fingerprint(Query(policy: first)), Fingerprint(Query(policy: second)));
    }

    [Fact]
    public void Api_version_changes_identity()
    {
        Assert.NotEqual(Fingerprint(Query(apiVersion: "7.1")), Fingerprint(Query(apiVersion: "6.0")));
    }

    [Fact]
    public void Time_range_changes_identity()
    {
        var withoutRange = Fingerprint(Query());
        var withMin = Fingerprint(Query(min: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var withMax = Fingerprint(Query(max: new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.NotEqual(withoutRange, withMin);
        Assert.NotEqual(withoutRange, withMax);
        Assert.NotEqual(withMin, withMax);
    }

    [Fact]
    public void Organization_and_project_change_identity()
    {
        Assert.NotEqual(Fingerprint(Query(organization: "https://dev.azure.com/a")), Fingerprint(Query(organization: "https://dev.azure.com/b")));
        Assert.NotEqual(Fingerprint(Query(project: "a")), Fingerprint(Query(project: "b")));
    }

    [Fact]
    public void Golden_vector_is_pinned()
    {
        var query = Query(resolved: [3, 1, 2, 2]);

        Assert.Equal(
            "4d12d64768d01c094e671b1328410d00b716755f6d9f807bd074d18332d1ea85",
            BuildQueryFingerprint.Compute(query));
    }

    [Fact]
    public void Fingerprint_Organization_is_case_sensitive()
    {
        Assert.NotEqual(Fingerprint(Query(organization: "acme")), Fingerprint(Query(organization: "Acme")));
    }

    [Fact]
    public void Fingerprint_Project_is_case_sensitive()
    {
        Assert.NotEqual(Fingerprint(Query(project: "acme")), Fingerprint(Query(project: "Acme")));
    }

    [Fact]
    public void Fingerprint_is_culture_invariant()
    {
        var query = Query(resolved: [3, 1, 2, 2], min: new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var expected = BuildQueryFingerprint.Compute(query);
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(expected, BuildQueryFingerprint.Compute(query));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    private static string Fingerprint(BuildQuery query) => BuildQueryFingerprint.Compute(query);

    private static BuildQuery Query(
        string organization = "https://dev.azure.com/org",
        string project = "project",
        DateTimeOffset? min = null,
        DateTimeOffset? max = null,
        IReadOnlyList<int>? resolved = null,
        DetailPolicy policy = DetailPolicy.FillMissing,
        string apiVersion = "7.1",
        IReadOnlyList<int>? definitionIds = null,
        IReadOnlyList<string>? definitionNames = null)
        => new(organization, project, min, max, resolved ?? [], policy, apiVersion)
        {
            DefinitionIds = definitionIds ?? [],
            DefinitionNames = definitionNames ?? []
        };
}
