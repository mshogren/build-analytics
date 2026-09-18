using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.Tests.AzureDevOps;

public sealed class AdoErrorTextTests
{
    [Fact]
    public void Format_strips_host_and_query_from_the_request_path()
    {
        var text = AdoErrorText.Format(
            "Azure DevOps request failed with status 403",
            "https://dev.azure.com/org/project/_apis/build/builds?continuationToken=secret&$top=10",
            "corr-1");

        Assert.Contains("/org/project/_apis/build/builds", text, StringComparison.Ordinal);
        Assert.Contains("403", text, StringComparison.Ordinal);
        Assert.Contains("corr-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dev.azure.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("continuationToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_keeps_a_relative_path_and_omits_a_missing_correlation_id()
    {
        var text = AdoErrorText.Format("failed", "/project/_apis/build/builds/42", null);

        Assert.Equal("failed for '/project/_apis/build/builds/42'.", text);
    }

    [Fact]
    public void Format_falls_back_to_root_for_a_blank_path()
    {
        Assert.Contains("'/'", AdoErrorText.Format("failed", "   ", null), StringComparison.Ordinal);
    }
}
