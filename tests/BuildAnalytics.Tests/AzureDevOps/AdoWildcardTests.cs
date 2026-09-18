using BuildAnalytics.App.AzureDevOps;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoWildcardTests
{
    [Theory]
    [InlineData("*build*", "MyBuild", true)]
    [InlineData("*build*", "MYBUILD-PR", true)]
    [InlineData("ci-?", "ci-1", true)]
    [InlineData("ci-?", "ci-12", false)]
    [InlineData("build", "MyBuild", false)]
    [InlineData("build", "build", true)]
    public void Wildcard_matches_anchored_and_case_insensitively(string pattern, string candidate, bool expected)
        => Assert.Equal(expected, AdoWildcard.ToRegex(pattern).IsMatch(candidate));

    [Fact]
    public void Anchoring_rejects_a_trailing_newline()
    {
        var matches = AdoWildcard.ToRegex("build").IsMatch("build\n");

        Assert.False(matches);
    }

    [Theory]
    [InlineData("a.b", "a.b", true)]
    [InlineData("a.b", "axb", false)]
    [InlineData("a+b", "a+b", true)]
    [InlineData("a+b", "aab", false)]
    [InlineData("(x)", "(x)", true)]
    [InlineData("(x)", "x", false)]
    [InlineData("a|b", "a|b", true)]
    [InlineData("a|b", "a", false)]
    public void Metacharacters_other_than_star_and_question_are_literal(string pattern, string candidate, bool expected)
        => Assert.Equal(expected, AdoWildcard.ToRegex(pattern).IsMatch(candidate));
}
