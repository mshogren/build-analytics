using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildAnalytics.App.Cli;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;
using BuildAnalytics.Tests.AzureDevOps;
using BuildAnalytics.Tests.Storage;

namespace BuildAnalytics.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Retrieve_success_writes_a_completed_manifest_and_uses_basic_auth()
    {
        using var root = new TempOutputRoot();
        AuthenticationHeaderValue? captured = null;
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(request =>
        {
            captured = request.Headers.Authorization;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "value": [] }""", Encoding.UTF8, "application/json")
            };
        });

        var console = new CapturingConsole();
        var factory = new CountingHandlerFactory(() => handler);
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, factory, delay: new FakeDelayScheduler());

        var code = await app.RunAsync(
            ["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal("Basic", captured!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(":secret")), captured.Parameter);

        using var reader = FileManifestStore.OpenReadOnly(root.Path);
        var manifest = await reader.TryReadAsync(CancellationToken.None);
        Assert.Equal(ManifestStatus.Completed, manifest!.Status);
    }

    [Fact]
    public async Task Retrieve_without_pat_fails_before_touching_the_transport()
    {
        using var root = new TempOutputRoot();
        var factory = new CountingHandlerFactory(() => new ScriptedHttpMessageHandler());
        var app = new CliApplication(new FakeCredentialProvider(null), new CapturingConsole(), factory);

        var code = await app.RunAsync(
            ["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task Retrieve_paused_returns_1_and_surfaces_structured_pause_fields()
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
            ["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(1, code);
        var error = string.Join("\n", console.Stderr);
        Assert.Contains("paused", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RetryAfterTooLong", error, StringComparison.Ordinal);
        Assert.Contains("retryAfter", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Rerun to resume", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retrieve_disposes_the_transport_after_the_run()
    {
        using var root = new TempOutputRoot();
        HttpMessageHandler? created = null;
        var factory = new CountingHandlerFactory(() =>
        {
            created = new TrackingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "value": [] }""", Encoding.UTF8, "application/json")
            });
            return created;
        });

        var app = new CliApplication(new FakeCredentialProvider("secret"), new CapturingConsole(), factory, delay: new FakeDelayScheduler());
        var code = await app.RunAsync(
            ["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.True(((TrackingHandler)created!).Disposed);
    }

    [Fact]
    public async Task Retrieve_cancellation_propagates_to_the_caller()
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
                ["retrieve", "--org", "https://dev.azure.com/org", "--project", "p", "--output-root", root.Path],
                cts.Token));
    }

    [Fact]
    public async Task Report_success_writes_the_workbook_and_creates_no_http_handler()
    {
        using var root = new TempOutputRoot();
        await WriteCompletedRootAsync(root.Path);

        var factory = new CountingHandlerFactory(() => throw new InvalidOperationException("report must not use the network"));
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, factory);
        var outputPath = Path.Combine(root.Path, "report.xlsx");

        var code = await app.RunAsync(
            ["report", "--output-root", root.Path, "--out", outputPath],
            CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(0, factory.Created);
        Assert.True(File.Exists(outputPath));
        Assert.Contains(outputPath, console.Stdout);
    }

    [Fact]
    public async Task Report_without_a_completed_manifest_returns_1()
    {
        using var root = new TempOutputRoot();
        var app = new CliApplication(new FakeCredentialProvider("secret"), new CapturingConsole(), new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()));

        var code = await app.RunAsync(["report", "--output-root", root.Path], CancellationToken.None);

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Usage_error_returns_2_and_writes_usage_to_stderr()
    {
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider("secret"), console, new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()));

        var code = await app.RunAsync(["retrieve", "--project", "p", "--output-root", "r"], CancellationToken.None);

        Assert.Equal(2, code);
        Assert.Contains("retrieve", string.Join("\n", console.Stderr), StringComparison.Ordinal);
        Assert.Empty(console.Stdout);
    }

    [Fact]
    public async Task No_args_prints_usage_to_stdout_and_returns_0()
    {
        var console = new CapturingConsole();
        var app = new CliApplication(new FakeCredentialProvider(null), console, new CountingHandlerFactory(() => new ScriptedHttpMessageHandler()));

        var code = await app.RunAsync([], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("retrieve", string.Join("\n", console.Stdout), StringComparison.Ordinal);
    }

    private static async Task WriteCompletedRootAsync(string root)
    {
        var manifest = new Manifest(
            Manifest.CurrentSchemaVersion,
            "fp",
            ManifestStatus.Completed,
            "cursor",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            [],
            [],
            []);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, BuildAnalyticsJson.Options));

        var runDirectory = Path.Combine(root, "runs", "1");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(runDirectory, "run.json"),
            JsonSerializer.SerializeToUtf8Bytes(TestRuns.Create(id: 1), BuildAnalyticsJson.Options));
    }
}
