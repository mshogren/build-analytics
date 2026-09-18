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
}
