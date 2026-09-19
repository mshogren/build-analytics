using System.Text.Json;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

/// <summary>
/// Durability invariants for the append-only run log (ADR-109/110). There is no manifest or
/// lock to fall behind, so the only crash artifacts are a durable append or a truncated tail.
/// </summary>
public sealed class StorageCrashTests
{
    [Fact]
    public async Task Crash_after_a_durable_append_leaves_the_run_readable()
    {
        using var root = new TempOutputRoot();

        await new FileRunStore(root.Path, new PhysicalFileOperations())
            .AppendAsync([TestRuns.Create(id: 11)], CancellationToken.None);

        // "Process crash" here: reopen a fresh store on the same directory.
        var reopened = new FileRunStore(root.Path, new PhysicalFileOperations());
        Assert.Equal([11], (await reopened.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
    }

    [Fact]
    public async Task Crash_mid_append_leaves_at_most_a_truncated_final_line()
    {
        using var root = new TempOutputRoot();
        var good = new FileRunStore(root.Path, new PhysicalFileOperations());
        await good.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        var partial = JsonSerializer.SerializeToUtf8Bytes(TestRuns.Create(id: 2), BuildAnalyticsJson.Options);
        var crashing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            beforeDelegate: (operation, path) =>
            {
                if (operation == FileOperation.Append)
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(partial, 0, partial.Length / 2);
                    throw new IOException("injected crash mid-append");
                }
            });
        var crashingStore = new FileRunStore(root.Path, crashing);

        await Assert.ThrowsAsync<IOException>(() => crashingStore.AppendAsync([TestRuns.Create(id: 2)], CancellationToken.None));

        var result = await good.ReadAllAsync(CancellationToken.None);
        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(1, result.MalformedLineCount);
    }

    [Fact]
    public async Task Append_after_a_crash_tail_does_not_glue_onto_the_fragment()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        await File.WriteAllTextAsync(logPath, "{\"schemaVersion\":1,\"id\":9", CancellationToken.None);

        await new FileRunStore(root.Path, new PhysicalFileOperations())
            .AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        var result = await new FileRunStore(root.Path, new PhysicalFileOperations())
            .ReadAllAsync(CancellationToken.None);
        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(1, result.MalformedLineCount);
    }
}
