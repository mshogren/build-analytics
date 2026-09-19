using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using BuildAnalytics.App.Cli;
using BuildAnalytics.Tests.AzureDevOps;
using BuildAnalytics.Tests.Storage;

namespace BuildAnalytics.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Run_success_retrieves_then_writes_the_report_and_uses_basic_auth()
    {
        using var root = new TempOutputRoot();
        AuthenticationHeaderValue? captured = null;
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(request =>
        {
            captured = request.Headers.Authorization;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "count": 0, "value": [] }""", Encoding.UTF8, "application/json")
            };
        });

        var console = new CapturingConsole();
        var factory = new CountingHandlerFactory(() => handler);
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, factory, delay: new FakeDelayScheduler());
        var outputPath = Path.Combine(root.Path, "report.xlsx");

        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path, "--out", outputPath],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("Basic", captured!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":secret")), captured.Parameter);
        Assert.True(File.Exists(outputPath));
        Assert.Contains(outputPath, console.Stdout);
        Assert.Contains("Generating report...", console.Stderr);
        Assert.Contains("0 skipped (detail unavailable)", string.Join("\n", console.Stderr), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_without_pat_fails_before_touching_the_transport_or_clearing_files()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var reportPath = Path.Combine(root.Path, "timing-report.xlsx");
        File.WriteAllText(logPath, "{\"schemaVersion\":1,\"id\":99}\n");
        File.WriteAllText(reportPath, "previous");

        var factory = new CountingHandlerFactory(() => new ScriptedHttpMessageHandler());
        var app = new CliApplication(new FakeCredentialProvider(null), new CapturingConsole(), factory);

        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(0, factory.Created);

        // The PAT check precedes the clear, so nothing on disk is touched.
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(reportPath));
    }

    [Fact]
    public async Task Run_stopped_returns_1_and_surfaces_the_stop_reason()
    {
        using var root = new TempOutputRoot();
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable, retryAfter: "61");

        var console = new CapturingConsole();
        var app = new CliApplication(
            new FakeCredentialProvider("secret"),
            console,
            new CountingHandlerFactory(() => handler),
            delay: new FakeDelayScheduler());

        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(1, code);
        var error = string.Join("\n", console.Stderr);
        Assert.Contains("stopped", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RetryAfterTooLong", error, StringComparison.Ordinal);
        Assert.Contains("retryAfter", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Generating report...", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_disposes_the_transport_after_the_run()
    {
        using var root = new TempOutputRoot();
        HttpMessageHandler? created = null;
        var factory = new CountingHandlerFactory(() =>
        {
            created = new TrackingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "count": 0, "value": [] }""", Encoding.UTF8, "application/json")
            });
            return created;
        });

        var app = new CliApplication(new FakeCredentialProvider("secret"), new CapturingConsole(), factory, delay: new FakeDelayScheduler());
        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.True(((TrackingHandler)created!).Disposed);
    }

    [Fact]
    public async Task Run_cancellation_propagates_to_the_caller()
    {
        using var root = new TempOutputRoot();
        var app = new CliApplication(
            new FakeCredentialProvider("secret"),
            new CapturingConsole(),
            new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()),
            delay: new FakeDelayScheduler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => app.RunAsync(
                ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
                cts.Token));
    }

    [Fact]
    public async Task Run_clears_the_previous_log_and_report_but_keeps_unrelated_files()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var reportPath = Path.Combine(root.Path, "report.xlsx");
        var unrelated = Path.Combine(root.Path, "keep-me.txt");
        File.WriteAllText(logPath, "{\"schemaVersion\":1,\"id\":99}\n");
        File.WriteAllText(reportPath, "stale");
        File.WriteAllText(unrelated, "keep");

        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("""{ "count": 0, "value": [] }""");
        var app = new CliApplication(new FakeCredentialProvider("secret"), new CapturingConsole(), new CountingHandlerFactory(() => handler), delay: new FakeDelayScheduler());

        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path, "--out", reportPath],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.False(File.Exists(logPath));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(reportPath));
    }

    [Fact]
    public async Task Usage_error_returns_2_and_writes_usage_to_stderr()
    {
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()));

        var code = await app.RunAsync(["--project", "p", "--output-root", "r"], CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Contains("--org", string.Join("\n", console.Stderr), StringComparison.Ordinal);
        Assert.Empty(console.Stdout);
    }

    [Fact]
    public async Task No_args_prints_usage_to_stdout_and_returns_0()
    {
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider(null), console, new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()));

        var code = await app.RunAsync([], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("--org", string.Join("\n", console.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_writes_a_line_per_page_and_quiet_suppresses_it()
    {
        using var root = new TempOutputRoot();
        using var quietRoot = new TempOutputRoot();
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("""{ "count": 0, "value": [] }""");
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, new CountingHandlerFactory(() => handler), delay: new FakeDelayScheduler());

        var code = await app.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(0, code);
        var stderr = string.Join("\n", console.Stderr);
        Assert.Contains("Retrieving page 1 - 0 builds", stderr, StringComparison.Ordinal);
        Assert.Contains("Generating report...", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(console.Stdout, line => line.Contains("page", StringComparison.OrdinalIgnoreCase));

        var quietHandler = new ScriptedHttpMessageHandler();
        quietHandler.EnqueueJson("""{ "count": 0, "value": [] }""");
        var quietConsole = new CapturingConsole();
        var quietApp = new CliApplication(new FakeCredentialProvider("secret"), quietConsole, new CountingHandlerFactory(() => quietHandler), delay: new FakeDelayScheduler());

        var quietCode = await quietApp.RunAsync(
            ["--org", "https://dev.azure.com/org", "--project", "p", "--output-root", quietRoot.Path, "--quiet"],
            CancellationToken.None);

        Assert.Equal(0, quietCode);
        Assert.Empty(quietConsole.Stderr);
    }

    [Fact]
    public async Task Run_can_take_its_required_values_from_config()
    {
        using var root = new TempOutputRoot();
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("""{ "count": 0, "value": [] }""");
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, new CountingHandlerFactory(() => handler), delay: new FakeDelayScheduler());
        var config = new BuildAnalyticsConfig(Organization: "https://dev.azure.com/org", Project: "p", OutputRoot: root.Path);

        var code = await app.RunAsync(["--quiet"], config, CancellationToken.None);

        Assert.Equal(0, code);
    }
}
