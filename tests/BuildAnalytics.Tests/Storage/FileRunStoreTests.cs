using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.Tests.Storage;

public sealed class FileRunStoreTests
{
    [Fact]
    public async Task Write_then_TryRead_round_trips()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        var run = TestRuns.Create(id: 5, buildNumber: "b5");

        await store.WriteAsync(run, CancellationToken.None);

        Assert.Equal(run, await store.TryReadAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task TryRead_missing_returns_null()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(42, CancellationToken.None));
    }

    [Fact]
    public async Task Write_uses_canonical_runs_id_run_json_path()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.WriteAsync(TestRuns.Create(id: 7, definitionName: "ci"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root.Path, "runs", "7", "run.json")));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "runs", "7__ci")));
    }

    [Fact]
    public async Task Upsert_by_id_overwrites_and_does_not_duplicate()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.WriteAsync(TestRuns.Create(id: 5, buildNumber: "first"), CancellationToken.None);
        await store.WriteAsync(TestRuns.Create(id: 5, buildNumber: "second"), CancellationToken.None);

        Assert.Equal("second", (await store.TryReadAsync(5, CancellationToken.None))!.BuildNumber);
        Assert.Equal([5], await store.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListRunIds_scans_runs_subdirs_sorted_and_ignores_incomplete()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await store.WriteAsync(TestRuns.Create(id: 3), CancellationToken.None);
        await store.WriteAsync(TestRuns.Create(id: 1), CancellationToken.None);

        Directory.CreateDirectory(Path.Combine(root.Path, "runs", "99"));
        Directory.CreateDirectory(Path.Combine(root.Path, "runs", "not-a-number"));
        File.WriteAllText(Path.Combine(root.Path, "runs", "stray.txt"), "x");

        Assert.Equal([1, 3], await store.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListRunIds_is_empty_when_runs_directory_absent()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        Assert.Empty(await store.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Durability_across_reopen()
    {
        using var root = new TempOutputRoot();
        var run = TestRuns.Create(id: 9);

        await new FileRunStore(root.Path, new PhysicalFileOperations()).WriteAsync(run, CancellationToken.None);

        var reopened = new FileRunStore(root.Path, new PhysicalFileOperations());
        Assert.Equal(run, await reopened.TryReadAsync(9, CancellationToken.None));
    }

    [Fact]
    public async Task Atomic_write_orders_temp_flush_rename_and_leaves_no_temp_file()
    {
        using var root = new TempOutputRoot();
        var recording = new RecordingFileOperations(new PhysicalFileOperations());
        var store = new FileRunStore(root.Path, recording);

        await store.WriteAsync(TestRuns.Create(id: 4), CancellationToken.None);

        Assert.Equal(
            [FileOperation.WriteTemp, FileOperation.FlushToDisk, FileOperation.Rename],
            recording.Calls.Select(call => call.Operation));

        var runDirectory = Path.Combine(root.Path, "runs", "4");
        Assert.Equal([Path.Combine(runDirectory, "run.json")], Directory.GetFiles(runDirectory));
    }

    [Fact]
    public async Task Crash_before_rename_leaves_no_run_file_and_no_duplicate_id()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("run.json", StringComparison.Ordinal)
                ? new IOException("injected crash before rename")
                : null);
        var store = new FileRunStore(root.Path, failing);

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(TestRuns.Create(id: 4), CancellationToken.None));

        Assert.Null(await store.TryReadAsync(4, CancellationToken.None));
        Assert.Empty(await store.ListRunIdsAsync(CancellationToken.None));
        var runDirectory = Path.Combine(root.Path, "runs", "4");
        if (Directory.Exists(runDirectory))
        {
            Assert.Empty(Directory.GetFiles(runDirectory));
        }
    }

    [Fact]
    public async Task TryRead_corrupt_run_file_throws_CorruptRunFileException()
    {
        using var root = new TempOutputRoot();
        var runDirectory = Path.Combine(root.Path, "runs", "5");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run.json"), "{ not json", CancellationToken.None);

        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<CorruptRunFileException>(() => store.TryReadAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task TryRead_schema_mismatch_throws_UnsupportedSchemaVersionException()
    {
        using var root = new TempOutputRoot();
        var runDirectory = Path.Combine(root.Path, "runs", "5");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, "run.json"),
            "{\"schemaVersion\":99,\"source\":\"list\",\"fetchedAt\":\"2024-01-01T00:00:00+00:00\",\"id\":5}",
            CancellationToken.None);

        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        var exception = await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => store.TryReadAsync(5, CancellationToken.None));

        Assert.Equal(BuildAnalytics.Core.Models.BuildRun.CurrentSchemaVersion, exception.Expected);
        Assert.Equal(99, exception.Actual);
    }

    [Fact]
    public async Task Concurrent_reader_never_sees_a_partial_run_file()
    {
        using var root = new TempOutputRoot();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var writing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            beforeDelegate: (operation, _) =>
            {
                if (operation == FileOperation.Rename)
                {
                    entered.Set();
                    release.Wait();
                }
            });

        var writer = new FileRunStore(root.Path, writing);
        var reader = new FileRunStore(root.Path, new PhysicalFileOperations());
        var run = TestRuns.Create(id: 4);

        var writeTask = Task.Run(() => writer.WriteAsync(run, CancellationToken.None));
        entered.Wait();

        Assert.Null(await reader.TryReadAsync(4, CancellationToken.None));

        release.Set();
        await writeTask;

        Assert.Equal(run, await reader.TryReadAsync(4, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_FaultBeforeTempWrite_NoTargetFile()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, _) => operation == FileOperation.WriteTemp ? new IOException("injected") : null);
        var store = new FileRunStore(root.Path, failing);

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(TestRuns.Create(id: 4), CancellationToken.None));

        var runDirectory = Path.Combine(root.Path, "runs", "4");
        Assert.False(File.Exists(Path.Combine(runDirectory, "run.json")));
        if (Directory.Exists(runDirectory))
        {
            Assert.Empty(Directory.GetFiles(runDirectory));
        }
    }

    [Fact]
    public async Task WriteAsync_FaultAfterRename_TargetCompleteReadable()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            afterDelegate: (operation, path) =>
            {
                if (operation == FileOperation.Rename && path.EndsWith("run.json", StringComparison.Ordinal))
                {
                    throw new IOException("injected after rename");
                }
            });
        var store = new FileRunStore(root.Path, failing);
        var run = TestRuns.Create(id: 4);

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(run, CancellationToken.None));

        Assert.Equal(run, await store.TryReadAsync(4, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"source\":\"list\",\"fetchedAt\":\"2024-01-01T00:00:00+00:00\",\"id\":0}")]
    [InlineData("{\"schemaVersion\":1,\"source\":\"list\",\"fetchedAt\":\"2024-01-01T00:00:00+00:00\",\"id\":7}")]
    public async Task RunFile_MissingId_Or_IdMismatchDirectory_ThrowsCorruptRunFile(string json)
    {
        using var root = new TempOutputRoot();
        var runDirectory = Path.Combine(root.Path, "runs", "5");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "run.json"), json, CancellationToken.None);

        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<CorruptRunFileException>(() => store.TryReadAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task RunArtifact_ContainsNoAbsolutePath()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.WriteAsync(TestRuns.Create(id: 4, sourceBranch: "refs/heads/main"), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root.Path, "runs", "4", "run.json"), CancellationToken.None);

        Assert.DoesNotContain(root.Path, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryRead_non_positive_run_id_throws()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryReadAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryReadAsync(-1, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_non_positive_run_id_throws()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.WriteAsync(TestRuns.Create(id: 0), CancellationToken.None));
    }

    [Fact]
    public async Task ListRunIds_skips_non_positive_directories()
    {
        using var root = new TempOutputRoot();
        var store = new FileRunStore(root.Path, new PhysicalFileOperations());
        await store.WriteAsync(TestRuns.Create(id: 2), CancellationToken.None);

        foreach (var name in new[] { "0", "-1" })
        {
            var directory = Path.Combine(root.Path, "runs", name);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "run.json"),
                $"{{\"schemaVersion\":1,\"source\":\"list\",\"fetchedAt\":\"2024-01-01T00:00:00+00:00\",\"id\":{(name == "-1" ? 1 : 0)}}}",
                CancellationToken.None);
        }

        Assert.Equal([2], await store.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_FlushInvokedOnNonEmptyTempBeforeRename()
    {
        using var root = new TempOutputRoot();
        long flushedLength = -1;
        var recording = new RecordingFileOperations(
            new PhysicalFileOperations(),
            beforeDelegate: (operation, path) =>
            {
                if (operation == FileOperation.FlushToDisk)
                {
                    flushedLength = new FileInfo(path).Length;
                }
            });
        var store = new FileRunStore(root.Path, recording);

        await store.WriteAsync(TestRuns.Create(id: 4), CancellationToken.None);

        Assert.True(flushedLength > 0, $"Flush must run on a non-empty temp file (observed {flushedLength}).");
        Assert.Equal(FileOperation.Rename, recording.Calls[^1].Operation);
    }

    [Fact]
    public Task WriteAsync_FaultAtWriteTemp_PreviousTargetIntact_NoTempLeak()
        => AssertRunOverwriteFaultPreservesTarget(FileOperation.WriteTemp);

    [Fact]
    public Task WriteAsync_FaultAtFlush_PreviousTargetIntact_NoTempLeak()
        => AssertRunOverwriteFaultPreservesTarget(FileOperation.FlushToDisk);

    private static async Task AssertRunOverwriteFaultPreservesTarget(FileOperation failAt)
    {
        using var root = new TempOutputRoot();
        var initial = new FileRunStore(root.Path, new PhysicalFileOperations());
        var first = TestRuns.Create(id: 4, buildNumber: "first");
        await initial.WriteAsync(first, CancellationToken.None);

        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == failAt && path.Contains("run.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);
        var store = new FileRunStore(root.Path, failing);

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(TestRuns.Create(id: 4, buildNumber: "second"), CancellationToken.None));

        Assert.Equal(first, await initial.TryReadAsync(4, CancellationToken.None));
        var runDirectory = Path.Combine(root.Path, "runs", "4");
        Assert.DoesNotContain(Directory.GetFiles(runDirectory), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
