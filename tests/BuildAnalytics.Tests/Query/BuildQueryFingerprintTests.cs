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
        var query = Query();
        Assert.Equal(BuildQueryFingerprint.Compute(query), BuildQueryFingerprint.Compute(query));
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
    public void Organization_and_project_change_identity()
    {
        Assert.NotEqual(Fingerprint(Query(organization: "https://dev.azure.com/a")), Fingerprint(Query(organization: "https://dev.azure.com/b")));
        Assert.NotEqual(Fingerprint(Query(project: "a")), Fingerprint(Query(project: "b")));
    }

    [Fact]
    public void Golden_vector_is_pinned()
    {
        var query = Query();

        Assert.Equal(
            "656df9d586fd2bf5aa0a467c426f2f9df50ae65c16f39fc64844f777d21ae9a4",
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
        var query = Query();
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
        DetailPolicy policy = DetailPolicy.FillMissing,
        string apiVersion = "7.1")
        => new(organization, project, policy, apiVersion);
}
