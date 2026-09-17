using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsToolTests
{
    [Fact]
    public async Task HelpCommand_WritesHelpText()
    {
        var originalOut = Console.Out;
        await using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            await BuildAnalyticsTool.RunAsync(new[] { "help" });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("build-analytics", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("export", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spreadsheet", output, StringComparison.OrdinalIgnoreCase);
    }
}
