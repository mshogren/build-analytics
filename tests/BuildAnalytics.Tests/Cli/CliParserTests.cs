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
        Assert.True(result.IsHelp);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_tokens_are_help(string token)
    {
        var result = CliParser.Parse([token]);

        Assert.True(result.IsHelp);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Requires_org_project_and_output_root()
    {
        Assert.True(CliParser.Parse(["--project", "p", "--output-root", "r"]).IsError);
        Assert.True(CliParser.Parse(["--org", "o", "--output-root", "r"]).IsError);
        Assert.True(CliParser.Parse(["--org", "o", "--project", "p"]).IsError);
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var result = CliParser.Parse(["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", "root"]);

        Assert.False(result.IsError);
        var options = result.Options!;
        Assert.Equal("7.1", options.ApiVersion);
        Assert.Equal(int.MaxValue, options.MaxRuns);
        Assert.False(options.Quiet);
        Assert.Equal(Path.Combine("root", "timing-report.xlsx"), options.OutputPath);
    }

    [Fact]
    public void Parses_all_options()
    {
        var result = CliParser.Parse(
        [
            "--org", "https://dev.azure.com/org",
            "--project", "p",
            "--output-root", "root",
            "--out", "custom.xlsx",
            "--max-runs", "50",
            "--api-version", "6.0",
            "--quiet"
        ]);

        var options = result.Options!;
        Assert.Equal("custom.xlsx", options.OutputPath);
        Assert.Equal(50, options.MaxRuns);
        Assert.Equal("6.0", options.ApiVersion);
        Assert.True(options.Quiet);
    }

    [Theory]
    [InlineData("--max-runs", "-1")]
    [InlineData("--max-runs", "abc")]
    public void Rejects_invalid_values(string flag, string value)
    {
        var result = CliParser.Parse(["--org", "o", "--project", "p", "--output-root", "r", flag, value]);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("retrieve")]
    [InlineData("report")]
    [InlineData("--from")]
    [InlineData("--to")]
    [InlineData("--page-size")]
    [InlineData("--definition-id")]
    [InlineData("--pat")]
    public void Verbs_and_legacy_flags_are_usage_errors(string token)
    {
        Assert.True(CliParser.Parse([token, "x"]).IsError);
    }

    [Fact]
    public void Inline_flag_equals_form_is_supported()
    {
        var result = CliParser.Parse(["--org=https://dev.azure.com/org", "--project=p", "--output-root=root"]);

        Assert.False(result.IsError);
        Assert.Equal("https://dev.azure.com/org", result.Options!.Organization);
        Assert.Equal("p", result.Options.Project);
        Assert.Equal("root", result.Options.OutputRoot);
    }

    [Fact]
    public void Config_supplies_defaults_and_cli_overrides_them()
    {
        var config = new BuildAnalyticsConfig(
            Organization: "https://dev.azure.com/config",
            Project: "config-project",
            OutputRoot: "config-root",
            ApiVersion: "6.0",
            MaxRuns: 10,
            Quiet: true,
            Out: "config.xlsx");

        var fromConfig = CliParser.Parse(["--quiet"], config).Options!;
        Assert.Equal("https://dev.azure.com/config", fromConfig.Organization);
        Assert.Equal("config-project", fromConfig.Project);
        Assert.Equal("6.0", fromConfig.ApiVersion);
        Assert.Equal(10, fromConfig.MaxRuns);
        Assert.True(fromConfig.Quiet);
        Assert.Equal("config.xlsx", fromConfig.OutputPath);

        var overridden = CliParser.Parse(
            ["--org", "https://dev.azure.com/cli", "--max-runs", "3"],
            config).Options!;
        Assert.Equal("https://dev.azure.com/cli", overridden.Organization);
        Assert.Equal(3, overridden.MaxRuns);
    }

    [Fact]
    public void Config_can_satisfy_required_options()
    {
        var config = new BuildAnalyticsConfig(Organization: "o", Project: "p", OutputRoot: "r");

        Assert.False(CliParser.Parse(["--quiet"], config).IsError);
    }

    [Fact]
    public void Parse_accepts_config_and_does_no_io()
    {
        // The file does not exist; the pure parser must not touch the filesystem.
        var result = CliParser.Parse(
            ["--org", "o", "--project", "p", "--output-root", "r", "--config", "missing-does-not-exist.json"]);

        Assert.False(result.IsError);
    }
}
