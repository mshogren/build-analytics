using BuildAnalytics.App.Cli;
using BuildAnalytics.Core.Query;

namespace BuildAnalytics.Tests.Cli;

public sealed class CliParserTests
{
    [Fact]
    public void No_args_is_help()
    {
        var result = CliParser.Parse([]);

        Assert.False(result.IsError);
        Assert.Equal(CliVerb.Help, result.Verb);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_tokens_are_help(string token)
    {
        var result = CliParser.Parse([token]);

        Assert.Equal(CliVerb.Help, result.Verb);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Retrieve_requires_org_project_and_output_root()
    {
        Assert.True(CliParser.Parse(["retrieve", "--project", "p", "--output-root", "r"]).IsError);
        Assert.True(CliParser.Parse(["retrieve", "--org", "o", "--output-root", "r"]).IsError);
        Assert.True(CliParser.Parse(["retrieve", "--org", "o", "--project", "p"]).IsError);
    }

    [Fact]
    public void Retrieve_defaults_match_the_spec()
    {
        var result = CliParser.Parse(["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", "root"]);

        Assert.False(result.IsError);
        var options = result.Retrieve!;
        Assert.Equal(DetailPolicy.ListOnly, options.DetailPolicy);
        Assert.Equal("7.1", options.ApiVersion);
        Assert.Equal(1000, options.PageSize);
        Assert.Equal(int.MaxValue, options.MaxRuns);
        Assert.False(options.Quiet);
        Assert.Null(options.MinTime);
        Assert.Empty(options.DefinitionIds);
        Assert.Empty(options.DefinitionGlobs);
    }

    [Fact]
    public void Retrieve_parses_all_options()
    {
        var result = CliParser.Parse(
        [
            "retrieve",
            "--org", "https://dev.azure.com/org",
            "--project", "p",
            "--output-root", "root",
            "--from", "2024-01-01T00:00:00Z",
            "--to", "2024-02-01T00:00:00Z",
            "--definition-id", "1,2",
            "--definition-id", "3",
            "--definition", "ci-*",
            "--detail", "fill-missing",
            "--max-runs", "50",
            "--page-size", "200",
            "--api-version", "6.0",
            "--quiet"
        ]);

        var options = result.Retrieve!;
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), options.MinTime);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), options.MaxTime);
        Assert.Equal([1, 2, 3], options.DefinitionIds);
        Assert.Equal(["ci-*"], options.DefinitionGlobs);
        Assert.Equal(DetailPolicy.FillMissing, options.DetailPolicy);
        Assert.Equal(50, options.MaxRuns);
        Assert.Equal(200, options.PageSize);
        Assert.Equal("6.0", options.ApiVersion);
        Assert.True(options.Quiet);
    }

    [Theory]
    [InlineData("--detail", "bogus")]
    [InlineData("--max-runs", "-1")]
    [InlineData("--page-size", "0")]
    [InlineData("--from", "not-a-date")]
    [InlineData("--definition-id", "0")]
    [InlineData("--max-runs", "abc")]
    public void Retrieve_rejects_invalid_values(string flag, string value)
    {
        var result = CliParser.Parse(["retrieve", "--org", "o", "--project", "p", "--output-root", "r", flag, value]);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--pat")]
    [InlineData("--input-root")]
    [InlineData("--output")]
    [InlineData("--pool-ids")]
    [InlineData("--exclude-reasons")]
    [InlineData("--max-queue-wait")]
    [InlineData("--count-only")]
    [InlineData("--throttle-limit")]
    public void Legacy_flags_are_usage_errors(string legacyFlag)
    {
        Assert.True(CliParser.Parse(["retrieve", "--org", "o", "--project", "p", "--output-root", "r", legacyFlag, "x"]).IsError);
    }

    [Fact]
    public void Inline_flag_equals_form_is_supported()
    {
        var result = CliParser.Parse(["retrieve", "--org=https://dev.azure.com/org", "--project=p", "--output-root=root"]);

        Assert.False(result.IsError);
        Assert.Equal("https://dev.azure.com/org", result.Retrieve!.Organization);
        Assert.Equal("p", result.Retrieve.Project);
        Assert.Equal("root", result.Retrieve.OutputRoot);
    }

    [Fact]
    public void Report_defaults_out_under_the_output_root()
    {
        var result = CliParser.Parse(["report", "--output-root", "root"]);

        Assert.False(result.IsError);
        Assert.Equal("root", result.Report!.OutputRoot);
        Assert.Equal(Path.Combine("root", "timing-report.xlsx"), result.Report.OutputPath);
    }

    [Fact]
    public void Report_honours_explicit_out()
    {
        var result = CliParser.Parse(["report", "--output-root", "root", "--out", "custom.xlsx", "--quiet"]);

        Assert.Equal("custom.xlsx", result.Report!.OutputPath);
        Assert.True(result.Report.Quiet);
    }

    [Fact]
    public void Report_requires_output_root()
    {
        Assert.True(CliParser.Parse(["report"]).IsError);
    }

    [Fact]
    public void Unknown_command_is_a_usage_error()
    {
        Assert.True(CliParser.Parse(["frobnicate"]).IsError);
    }
}
