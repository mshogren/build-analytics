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
        Assert.Equal(int.MaxValue, options.MaxRuns);
        Assert.False(options.Quiet);
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
            "--detail", "fill-missing",
            "--max-runs", "50",
            "--api-version", "6.0",
            "--quiet"
        ]);

        var options = result.Retrieve!;
        Assert.Equal(DetailPolicy.FillMissing, options.DetailPolicy);
        Assert.Equal(50, options.MaxRuns);
        Assert.Equal("6.0", options.ApiVersion);
        Assert.True(options.Quiet);
    }

    [Theory]
    [InlineData("--detail", "bogus")]
    [InlineData("--max-runs", "-1")]
    [InlineData("--max-runs", "abc")]
    public void Retrieve_rejects_invalid_values(string flag, string value)
    {
        var result = CliParser.Parse(["retrieve", "--org", "o", "--project", "p", "--output-root", "r", flag, value]);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("--from")]
    [InlineData("--to")]
    [InlineData("--page-size")]
    [InlineData("--definition-id")]
    [InlineData("--definition")]
    [InlineData("--pat")]
    [InlineData("--input-root")]
    [InlineData("--output")]
    [InlineData("--pool-ids")]
    [InlineData("--exclude-reasons")]
    [InlineData("--max-queue-wait")]
    [InlineData("--count-only")]
    [InlineData("--throttle-limit")]
    public void Removed_and_legacy_flags_are_usage_errors(string legacyFlag)
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
    public void Config_supplies_retrieve_defaults_and_cli_overrides_them()
    {
        var config = new BuildAnalyticsConfig(
            Organization: "https://dev.azure.com/config",
            Project: "config-project",
            OutputRoot: "config-root",
            ApiVersion: "6.0",
            Detail: "fill-missing",
            MaxRuns: 10,
            Quiet: true);

        var fromConfig = CliParser.Parse(["retrieve"], config).Retrieve!;
        Assert.Equal("https://dev.azure.com/config", fromConfig.Organization);
        Assert.Equal("config-project", fromConfig.Project);
        Assert.Equal("config-root", fromConfig.OutputRoot);
        Assert.Equal("6.0", fromConfig.ApiVersion);
        Assert.Equal(DetailPolicy.FillMissing, fromConfig.DetailPolicy);
        Assert.Equal(10, fromConfig.MaxRuns);
        Assert.True(fromConfig.Quiet);

        var overridden = CliParser.Parse(
            ["retrieve", "--org", "https://dev.azure.com/cli", "--max-runs", "3", "--detail", "list"],
            config).Retrieve!;
        Assert.Equal("https://dev.azure.com/cli", overridden.Organization);
        Assert.Equal(3, overridden.MaxRuns);
        Assert.Equal(DetailPolicy.ListOnly, overridden.DetailPolicy);
    }

    [Fact]
    public void Config_can_satisfy_required_retrieve_options()
    {
        var config = new BuildAnalyticsConfig(Organization: "o", Project: "p", OutputRoot: "r");

        Assert.False(CliParser.Parse(["retrieve"], config).IsError);
    }

    [Fact]
    public void Invalid_config_detail_is_a_usage_error()
    {
        var config = new BuildAnalyticsConfig(Organization: "o", Project: "p", OutputRoot: "r", Detail: "nonsense");

        Assert.True(CliParser.Parse(["retrieve"], config).IsError);
    }

    [Fact]
    public void Parse_accepts_config_and_does_no_io()
    {
        // The file does not exist; the pure parser must not touch the filesystem.
        var result = CliParser.Parse(
            ["retrieve", "--org", "o", "--project", "p", "--output-root", "r", "--config", "missing-does-not-exist.json"]);

        Assert.False(result.IsError);
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
    public void Report_reads_output_root_and_out_from_config()
    {
        var config = new BuildAnalyticsConfig(OutputRoot: "config-root", Out: "config.xlsx", Quiet: true);

        var result = CliParser.Parse(["report"], config).Report!;

        Assert.Equal("config-root", result.OutputRoot);
        Assert.Equal("config.xlsx", result.OutputPath);
        Assert.True(result.Quiet);
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
