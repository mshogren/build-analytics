using System.Text.Json;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

public sealed class FileRunStoreTests
{
    [Fact]
    public async Task Append_then_ReadAll_round_trips()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        var run = TestRuns.Create(id: 5, buildNumber: "b5");

        await store.AppendAsync([run], CancellationToken.None);

        var result = await store.ReadAllAsync(CancellationToken.None);
        Assert.Equal([run], result.Runs);
        Assert.Equal(0, result.MalformedLineCount);
        Assert.Equal(0, result.UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task ReadAll_absent_file_is_empty()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        var result = await store.ReadAllAsync(CancellationToken.None);

        Assert.Empty(result.Runs);
        Assert.Equal(0, result.MalformedLineCount);
        Assert.Equal(0, result.UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task Append_uses_canonical_runs_jsonl_path()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync([TestRuns.Create(id: 7, definitionName: "ci")], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root.Path, "runs.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "runs")));
    }

    [Fact]
    public async Task Append_writes_one_compact_newline_terminated_line_per_run()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync([TestRuns.Create(id: 1), TestRuns.Create(id: 2)], CancellationToken.None);

        var lines = (await File.ReadAllTextAsync(Path.Combine(root.Path, "runs.jsonl"), CancellationToken.None)).Split('\n');
        Assert.Equal(3, lines.Length); // two lines + trailing empty segment
        Assert.Equal(string.Empty, lines[^1]);
        Assert.All(lines[..2], line => Assert.StartsWith("{\"schemaVersion\":", line, StringComparison.Ordinal));
        Assert.All(lines[..2], line => Assert.DoesNotContain("  ", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_empty_list_does_not_create_the_file()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync([], CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root.Path, "runs.jsonl")));
    }

    [Fact]
    public async Task Append_dedupes_last_line_wins()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync([TestRuns.Create(id: 5, buildNumber: "first")], CancellationToken.None);
        await store.AppendAsync([TestRuns.Create(id: 5, buildNumber: "second")], CancellationToken.None);

        var runs = (await store.ReadAllAsync(CancellationToken.None)).Runs;
        Assert.Single(runs);
        Assert.Equal("second", runs[0].BuildNumber);
    }

    [Fact]
    public async Task Append_last_line_wins_within_a_single_batch()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync(
            [TestRuns.Create(id: 5, buildNumber: "first"), TestRuns.Create(id: 5, buildNumber: "second")],
            CancellationToken.None);

        var runs = (await store.ReadAllAsync(CancellationToken.None)).Runs;
        Assert.Single(runs);
        Assert.Equal("second", runs[0].BuildNumber);
    }

    [Fact]
    public async Task ReadAll_sorts_by_id_and_tolerates_out_of_order_appends()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.AppendAsync([TestRuns.Create(id: 3)], CancellationToken.None);
        await store.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        Assert.Equal([1, 3], (await store.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
    }

    [Fact]
    public async Task ReadAll_tolerates_a_truncated_final_line()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);
        await AppendRawAsync(Path.Combine(root.Path, "runs.jsonl"), "{\"schemaVersion\":1,\"id\":2");

        var result = await store.ReadAllAsync(CancellationToken.None);

        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(1, result.MalformedLineCount);
        Assert.Equal(0, result.UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task ReadAll_counts_and_skips_malformed_lines()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var valid = JsonSerializer.Serialize(TestRuns.Create(id: 1), BuildAnalyticsJson.Options);
        await File.WriteAllTextAsync(logPath, "{ not json\n" + valid + "\n", CancellationToken.None);

        var result = await new FileRunStore(root.Path, new PhysicalFileOperations()).ReadAllAsync(CancellationToken.None);

        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(1, result.MalformedLineCount);
        Assert.Equal(0, result.UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task ReadAll_counts_and_excludes_unsupported_schema_lines()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var newer = JsonSerializer.Serialize(TestRuns.Create(id: 2) with { SchemaVersion = 99 }, BuildAnalyticsJson.Options);
        var valid = JsonSerializer.Serialize(TestRuns.Create(id: 1), BuildAnalyticsJson.Options);
        await File.WriteAllTextAsync(logPath, newer + "\n" + valid + "\n", CancellationToken.None);

        var result = await new FileRunStore(root.Path, new PhysicalFileOperations()).ReadAllAsync(CancellationToken.None);

        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(0, result.MalformedLineCount);
        Assert.Equal(1, result.UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task ReadAll_ignores_blank_lines()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var valid = JsonSerializer.Serialize(TestRuns.Create(id: 1), BuildAnalyticsJson.Options);
        await File.WriteAllTextAsync(logPath, "\n" + valid + "\n\n", CancellationToken.None);

        var result = await new FileRunStore(root.Path, new PhysicalFileOperations()).ReadAllAsync(CancellationToken.None);

        Assert.Equal([1], result.Runs.Select(run => run.Id));
        Assert.Equal(0, result.MalformedLineCount);
    }

    [Fact]
    public async Task Append_uses_the_durable_append_seam()
    {
        using var root = new TempOutputRoot();
        var recording = new RecordingFileOperations(new PhysicalFileOperations());
        var store = new FileRunStore(root.Path, recording);

        await store.AppendAsync([TestRuns.Create(id: 4)], CancellationToken.None);

        Assert.Equal([FileOperation.Append], recording.Calls.Select(call => call.Operation));
        Assert.Equal(Path.Combine(root.Path, "runs.jsonl"), recording.Calls[0].Path);
    }

    [Fact]
    public async Task ReplaceAll_rewrites_the_file_compactly()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.AppendAsync([TestRuns.Create(id: 1, buildNumber: "old")], CancellationToken.None);
        await store.AppendAsync([TestRuns.Create(id: 1, buildNumber: "new"), TestRuns.Create(id: 2)], CancellationToken.None);

        await store.ReplaceAllAsync([TestRuns.Create(id: 1, buildNumber: "new"), TestRuns.Create(id: 2)], CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root.Path, "runs.jsonl"), CancellationToken.None);
        Assert.Equal(2, text.Split('\n').Length - 1);
        var runs = (await store.ReadAllAsync(CancellationToken.None)).Runs;
        Assert.Equal([1, 2], runs.Select(run => run.Id));
        Assert.Equal("new", runs[0].BuildNumber);
    }

    [Fact]
    public async Task ReplaceAll_orders_temp_flush_rename_and_leaves_no_temp_file()
    {
        using var root = new TempOutputRoot();
        var recording = new RecordingFileOperations(new PhysicalFileOperations());
        var store = new FileRunStore(root.Path, recording);

        await store.ReplaceAllAsync([TestRuns.Create(id: 2)], CancellationToken.None);

        Assert.Equal(
            [FileOperation.WriteTemp, FileOperation.FlushToDisk, FileOperation.Rename],
            recording.Calls.Select(call => call.Operation));
        Assert.DoesNotContain(Directory.GetFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceAll_failure_leaves_the_prior_file_intact()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("runs.jsonl", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);
        var failingStore = new FileRunStore(root.Path, failing);

        await Assert.ThrowsAsync<IOException>(() => failingStore.ReplaceAllAsync([TestRuns.Create(id: 2)], CancellationToken.None));

        Assert.Equal([1], (await store.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
        Assert.DoesNotContain(Directory.GetFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Durability_across_reopen()
    {
        using var root = new TempOutputRoot();
        var run = TestRuns.Create(id: 9);

        await new FileRunStore(root.Path, new PhysicalFileOperations()).AppendAsync([run], CancellationToken.None);

        var reopened = new FileRunStore(root.Path, new PhysicalFileOperations());
        Assert.Equal([run], (await reopened.ReadAllAsync(CancellationToken.None)).Runs);
    }

    [Fact]
    public async Task Concurrent_reader_is_consistent_while_append_is_in_progress()
    {
        using var root = new TempOutputRoot();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var block = false;
        var writing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            beforeDelegate: (operation, _) =>
            {
                if (block && operation == FileOperation.Append)
                {
                    entered.Set();
                    release.Wait();
                }
            });

        var writer = new FileRunStore(root.Path, writing);
        var reader = new FileRunStore(root.Path, new PhysicalFileOperations());
        await writer.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        block = true;
        var appendTask = Task.Run(() => writer.AppendAsync([TestRuns.Create(id: 2)], CancellationToken.None));
        entered.Wait();

        Assert.Equal([1], (await reader.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));

        release.Set();
        await appendTask;

        Assert.Equal([1, 2], (await reader.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
    }

    [Fact]
    public async Task RunArtifact_ContainsNoAbsolutePath()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.AppendAsync([TestRuns.Create(id: 4, sourceBranch: "refs/heads/main")], CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root.Path, "runs.jsonl"), CancellationToken.None);

        Assert.DoesNotContain(root.Path, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_non_positive_run_id_throws()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.AppendAsync([TestRuns.Create(id: 0)], CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceAll_non_positive_run_id_throws()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ReplaceAllAsync([TestRuns.Create(id: -1)], CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_line_is_not_counted_when_a_later_valid_line_has_the_same_id()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var bad = JsonSerializer.Serialize(TestRuns.Create(id: 2) with { SchemaVersion = 99 }, BuildAnalyticsJson.Options);
        var valid = JsonSerializer.Serialize(TestRuns.Create(id: 1), BuildAnalyticsJson.Options);
        var repaired = JsonSerializer.Serialize(TestRuns.Create(id: 2, buildNumber: "repaired"), BuildAnalyticsJson.Options);
        await File.WriteAllTextAsync(logPath, bad + "\n" + valid + "\n" + repaired + "\n", CancellationToken.None);

        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        var result = await store.ReadAllAsync(CancellationToken.None);

        Assert.Equal(0, result.UnsupportedSchemaLineCount);
        Assert.Equal([1, 2], result.Runs.Select(run => run.Id));

        // A clean read permits compaction; the superseded bad line is then physically dropped.
        await store.ReplaceAllAsync(result.Runs, CancellationToken.None);

        var lines = (await File.ReadAllTextAsync(logPath, CancellationToken.None))
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(0, (await store.ReadAllAsync(CancellationToken.None)).UnsupportedSchemaLineCount);
    }

    [Fact]
    public async Task Append_after_a_truncated_tail_keeps_the_new_line_readable()
    {
        using var root = new TempOutputRoot();
        var logPath = Path.Combine(root.Path, "runs.jsonl");
        var valid = JsonSerializer.Serialize(TestRuns.Create(id: 1), BuildAnalyticsJson.Options);
        await File.WriteAllTextAsync(logPath, valid + "\n{\"schemaVersion\":1,\"id\":2", CancellationToken.None);

        await new FileRunStore(root.Path, new PhysicalFileOperations())
            .AppendAsync([TestRuns.Create(id: 3)], CancellationToken.None);

        var result = await new FileRunStore(root.Path, new PhysicalFileOperations())
            .ReadAllAsync(CancellationToken.None);
        Assert.Equal([1, 3], result.Runs.Select(run => run.Id));
        Assert.Equal(1, result.MalformedLineCount);
    }

    private static async Task AppendRawAsync(string path, string text)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, CancellationToken.None);
        stream.Flush(flushToDisk: true);
    }
}
