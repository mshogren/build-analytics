using BuildAnalytics.App.Cli;
using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.Tests.Cli;

public sealed class CliErrorTextTests
{
    [Fact]
    public void Typed_errors_keep_their_sanitized_message()
    {
        var write = new ReportingWriteException("report.xlsx", "the destination is a directory.");
        Assert.Equal(write.Message, CliErrorText.Describe(write));

        var adoSpecific = new AdoRequestException(403, "/project/_apis/build/builds", "corr-1");
        Assert.Equal(adoSpecific.Message, CliErrorText.Describe(adoSpecific));
    }

    [Fact]
    public void Storage_failures_are_generic()
    {
        var text = CliErrorText.Describe(new StorageException("failed at /home/node/secret/runs.jsonl"));

        Assert.DoesNotContain("/home", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_exceptions_are_type_only_with_no_stack_trace()
    {
        var text = CliErrorText.Describe(new InvalidOperationException("boom at /home/node/path"));

        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home", text, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", text, StringComparison.Ordinal);
    }
}
