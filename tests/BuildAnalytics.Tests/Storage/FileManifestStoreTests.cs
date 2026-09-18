using System.Globalization;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

public sealed class FileManifestStoreTests
{
    [Fact]
    public async Task TryRead_missing_returns_null()
    {
        using var root = new TempOutputRoot();
        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Commit_then_TryRead_round_trips_across_reopen()
    {
        using var root = new TempOutputRoot();
        var manifest = SampleManifest();

        using (var store = new FileManifestStore(root.Path, new PhysicalFileOperations()))
        {
            await store.CommitAsync(manifest, CancellationToken.None);
        }

        using var reopened = new FileManifestStore(root.Path, new PhysicalFileOperations());
        var read = await reopened.TryReadAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(manifest.SchemaVersion, read!.SchemaVersion);
        Assert.Equal(manifest.Fingerprint, read.Fingerprint);
        Assert.Equal(manifest.Status, read.Status);
        Assert.Equal(manifest.Cursor, read.Cursor);
        Assert.Equal(manifest.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public void Second_writer_on_same_root_throws_OutputRootInUse()
    {
        using var root = new TempOutputRoot();
        using var first = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Throws<OutputRootInUseException>(() => new FileManifestStore(root.Path, new PhysicalFileOperations()));
    }

    [Fact]
    public void Lock_NonLockIoFailure_ThrowsStorageException_NotOutputRootInUse()
    {
        using var root = new TempOutputRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "manifest.lock"));

        Assert.Throws<StorageException>(() => new FileManifestStore(root.Path, new PhysicalFileOperations()));
    }

    [Fact]
    public void Reopen_after_dispose_succeeds()
    {
        using var root = new TempOutputRoot();

        var first = new FileManifestStore(root.Path, new PhysicalFileOperations());
        first.Dispose();

        using var second = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Corrupt_manifest_is_quarantined_and_returns_null()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{ this is not json", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
        Assert.False(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Truncated_manifest_is_quarantined_and_returns_null()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{\"schemaVersion\":1,\"fingerprint\":", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
        Assert.False(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task Schema_mismatch_throws_typed_error_without_quarantine(int schemaVersion)
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, ValidManifestJson(schemaVersion), CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        var exception = await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(
            () => store.TryReadAsync(CancellationToken.None));

        Assert.Equal(Manifest.CurrentSchemaVersion, exception.Expected);
        Assert.Equal(schemaVersion, exception.Actual);
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Newer_valid_manifest_is_not_quarantined_as_corrupt()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, ValidManifestJson(99), CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => store.TryReadAsync(CancellationToken.None));
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task TryRead_UnsupportedSchemaVersion_WithOtherMissingFields_ThrowsUnsupported_NotQuarantined()
    {
        using var root = new TempOutputRoot();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), "{\"schemaVersion\":99}", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => store.TryReadAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Manifest_missing_schema_version_is_treated_as_corrupt()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{\"fingerprint\":\"fp\"}", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Error_messages_do_not_leak_absolute_paths()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{\"schemaVersion\":99}", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        var exception = await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(
            () => store.TryReadAsync(CancellationToken.None));

        Assert.DoesNotContain(root.Path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_is_atomic_and_orders_temp_flush_rename()
    {
        using var root = new TempOutputRoot();
        var recording = new RecordingFileOperations(new PhysicalFileOperations());
        using var store = new FileManifestStore(root.Path, recording);

        await store.CommitAsync(SampleManifest(), CancellationToken.None);

        Assert.Equal(
            [FileOperation.WriteTemp, FileOperation.FlushToDisk, FileOperation.Rename],
            recording.Calls.Select(call => call.Operation));
    }

    [Fact]
    public async Task Read_only_store_reads_while_writer_holds_lock()
    {
        using var root = new TempOutputRoot();
        using var writer = new FileManifestStore(root.Path, new PhysicalFileOperations());
        await writer.CommitAsync(SampleManifest(), CancellationToken.None);

        using var reader = FileManifestStore.OpenReadOnly(root.Path);
        var read = await reader.TryReadAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("fp-1", read!.Fingerprint);
    }

    [Fact]
    public async Task Read_only_store_cannot_commit()
    {
        using var root = new TempOutputRoot();
        using var reader = FileManifestStore.OpenReadOnly(root.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.CommitAsync(SampleManifest(), CancellationToken.None));
    }

    [Fact]
    public async Task Read_only_store_on_missing_root_returns_null()
    {
        using var root = new TempOutputRoot();
        using var reader = FileManifestStore.OpenReadOnly(Path.Combine(root.Path, "missing"));

        Assert.Null(await reader.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Read_only_corrupt_manifest_is_not_quarantined()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{ not json", CancellationToken.None);

        using var reader = FileManifestStore.OpenReadOnly(root.Path);

        Assert.Null(await reader.TryReadAsync(CancellationToken.None));
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Read_only_malformed_manifest_is_not_quarantined()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{\"fingerprint\":\"fp\"}", CancellationToken.None);

        using var reader = FileManifestStore.OpenReadOnly(root.Path);

        Assert.Null(await reader.TryReadAsync(CancellationToken.None));
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Read_only_wrong_schema_still_throws_without_quarantine()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, ValidManifestJson(99), CancellationToken.None);

        using var reader = FileManifestStore.OpenReadOnly(root.Path);

        await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => reader.TryReadAsync(CancellationToken.None));
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public void Lock_StreamDisposedOnPostOpenFailure()
    {
        using var root = new TempOutputRoot();

        Assert.Throws<StorageException>(() => new FileManifestStore(root.Path, new PhysicalFileOperations(), new ThrowingTimeProvider()));

        // The failed writer must have released the lock stream.
        using var second = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Quarantine_DoesNotOverwriteExistingQuarantineFile()
    {
        using var root = new TempOutputRoot();
        var preexisting = Path.Combine(root.Path, "manifest.corrupt-pre-existing.json");
        await File.WriteAllTextAsync(preexisting, "old", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), "{ not json", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.Null(await store.TryReadAsync(CancellationToken.None));

        Assert.True(File.Exists(preexisting));
        Assert.Equal("old", await File.ReadAllTextAsync(preexisting, CancellationToken.None));
        Assert.Equal(2, Directory.GetFiles(root.Path, "manifest.corrupt-*.json").Length);
    }

    [Fact]
    public async Task Quarantine_PreservesRawBytes()
    {
        using var root = new TempOutputRoot();
        const string corrupt = "{ broken json without closing";
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), corrupt, CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.Null(await store.TryReadAsync(CancellationToken.None));

        var quarantine = Assert.Single(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(quarantine, CancellationToken.None));
    }

    [Fact]
    public async Task Quarantine_DoesNotTouchExistingRuns()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        var run = TestRuns.Create(id: 11);
        await runStore.WriteAsync(run, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), "{ not json", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.Null(await store.TryReadAsync(CancellationToken.None));

        Assert.Equal(run, await runStore.TryReadAsync(11, CancellationToken.None));
    }

    [Fact]
    public async Task AfterQuarantine_NextCommitStartsCursorNull_AndRescanUpserts()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        await runStore.WriteAsync(TestRuns.Create(id: 11, buildNumber: "first"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), "{ not json", CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.Null(await store.TryReadAsync(CancellationToken.None));
        Assert.Equal([11], await runStore.ListRunIdsAsync(CancellationToken.None));

        var rebuilt = new Manifest(
            Manifest.CurrentSchemaVersion, "fp", ManifestStatus.InProgress, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, [], [], []);
        await store.CommitAsync(rebuilt, CancellationToken.None);
        await runStore.WriteAsync(TestRuns.Create(id: 11, buildNumber: "second"), CancellationToken.None);

        var read = await store.TryReadAsync(CancellationToken.None);
        Assert.Null(read!.Cursor);
        Assert.Equal([11], await runStore.ListRunIdsAsync(CancellationToken.None));
    }

    [Fact]
    public void Lock_FilePathIsManifestLock()
    {
        using var root = new TempOutputRoot();
        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.True(File.Exists(Path.Combine(root.Path, "manifest.lock")));
    }

    [Fact]
    public async Task Lock_ReleasedAfterWriteFailure()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);

        var first = new FileManifestStore(root.Path, failing);
        await Assert.ThrowsAsync<IOException>(() => first.CommitAsync(SampleManifest(), CancellationToken.None));
        first.Dispose();

        using var second = new FileManifestStore(root.Path, new PhysicalFileOperations());
        Assert.NotNull(second);
    }

    [Fact]
    public void StaleLock_DeadPid_IsReclaimed()
    {
        using var root = new TempOutputRoot();
        File.WriteAllText(Path.Combine(root.Path, "manifest.lock"), "999999\n2020-01-01T00:00:00.0000000+00:00");

        using (var store = new FileManifestStore(root.Path, new PhysicalFileOperations()))
        {
            Assert.NotNull(store);
        }

        var content = File.ReadAllText(Path.Combine(root.Path, "manifest.lock"));
        Assert.Contains(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), content, StringComparison.Ordinal);
        Assert.DoesNotContain("999999", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitAsync_Overwrite_Atomic_PreservesPriorOnFailure()
    {
        using var root = new TempOutputRoot();

        using (var initial = new FileManifestStore(root.Path, new PhysicalFileOperations()))
        {
            await initial.CommitAsync(SampleManifest(cursor: "a"), CancellationToken.None);
        }

        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);

        using (var store = new FileManifestStore(root.Path, failing))
        {
            await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(SampleManifest(cursor: "b"), CancellationToken.None));
        }

        using var reader = FileManifestStore.OpenReadOnly(root.Path);
        var read = await reader.TryReadAsync(CancellationToken.None);
        Assert.Equal("a", read!.Cursor);
    }

    [Fact]
    public async Task Store_InjectsNoPatOrAbsolutePath_Itself()
    {
        using var root = new TempOutputRoot();
        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        var manifest = new Manifest(
            Manifest.CurrentSchemaVersion, "fp", ManifestStatus.InProgress, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "boom", [], [], []);

        await store.CommitAsync(manifest, CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(root.Path, "manifest.json"), CancellationToken.None);
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pat\"", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(FileOperation.WriteTemp)]
    [InlineData(FileOperation.FlushToDisk)]
    public async Task Overwrite_fault_leaves_prior_manifest_intact_and_no_temp_file(FileOperation failAt)
    {
        using var root = new TempOutputRoot();

        using (var initial = new FileManifestStore(root.Path, new PhysicalFileOperations()))
        {
            await initial.CommitAsync(SampleManifest(cursor: "a"), CancellationToken.None);
        }

        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == failAt && path.Contains("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);

        using (var store = new FileManifestStore(root.Path, failing))
        {
            await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(SampleManifest(cursor: "b"), CancellationToken.None));
        }

        using var reader = FileManifestStore.OpenReadOnly(root.Path);
        Assert.Equal("a", (await reader.TryReadAsync(CancellationToken.None))!.Cursor);
        Assert.DoesNotContain(Directory.GetFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public Task Manifest_MissingFingerprint_Quarantined()
        => AssertManifestQuarantined(
            "{\"schemaVersion\":1,\"status\":\"in_progress\",\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}");

    [Fact]
    public Task Manifest_EmptyFingerprint_Quarantined()
        => AssertManifestQuarantined(
            "{\"schemaVersion\":1,\"fingerprint\":\"\",\"status\":\"in_progress\",\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}");

    [Fact]
    public Task Manifest_MissingStatus_Quarantined()
        => AssertManifestQuarantined(
            "{\"schemaVersion\":1,\"fingerprint\":\"fp\",\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}");

    [Fact]
    public Task Manifest_MissingCreatedAt_Quarantined()
        => AssertManifestQuarantined(
            "{\"schemaVersion\":1,\"fingerprint\":\"fp\",\"status\":\"in_progress\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}");

    [Fact]
    public Task Manifest_MissingUpdatedAt_Quarantined()
        => AssertManifestQuarantined(
            "{\"schemaVersion\":1,\"fingerprint\":\"fp\",\"status\":\"in_progress\",\"createdAt\":\"2024-01-01T00:00:00+00:00\"}");

    [Fact]
    public Task TryRead_CurrentSchemaVersion_MissingRequiredField_Quarantined()
        => AssertManifestQuarantined("{\"schemaVersion\":1,\"status\":\"in_progress\"}");

    [Fact]
    public async Task Manifest_with_extra_unknown_field_is_accepted()
    {
        using var root = new TempOutputRoot();
        var json = "{\"schemaVersion\":1,\"fingerprint\":\"fp\",\"status\":\"in_progress\",\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\",\"extra\":42}";
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), json, CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());
        var read = await store.TryReadAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("fp", read!.Fingerprint);
    }

    [Fact]
    public async Task Quarantine_HonorsCancellation()
    {
        using var root = new TempOutputRoot();
        var manifestPath = Path.Combine(root.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{ not json", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations(), new CancellingTimeProvider(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryReadAsync(cts.Token));

        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    [Fact]
    public async Task Quarantine_IoFailure_ThrowsStorageException()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempOutputRoot();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), "{ not json", CancellationToken.None);
        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        var originalMode = File.GetUnixFileMode(root.Path);
        try
        {
            File.SetUnixFileMode(root.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<StorageException>(() => store.TryReadAsync(CancellationToken.None));
        }
        finally
        {
            File.SetUnixFileMode(root.Path, originalMode);
        }
    }

    private static async Task AssertManifestQuarantined(string json)
    {
        using var root = new TempOutputRoot();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "manifest.json"), json, CancellationToken.None);

        using var store = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Null(await store.TryReadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(root.Path, "manifest.corrupt-*.json"));
    }

    private static Manifest SampleManifest(string cursor = "cursor-1")
        => new(
            Manifest.CurrentSchemaVersion,
            "fp-1",
            ManifestStatus.InProgress,
            cursor,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            null,
            [],
            [1, 2],
            ["ci"]);

    private static string ValidManifestJson(int schemaVersion)
        => $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "fingerprint": "fp",
          "status": "in_progress",
          "cursor": null,
          "createdAt": "2024-01-01T00:00:00+00:00",
          "updatedAt": "2024-01-01T00:00:00+00:00",
          "lastError": null,
          "failedRunIds": [],
          "definitionIds": [],
          "definitionNames": []
        }
        """;

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("clock failure");
    }

    private sealed class CancellingTimeProvider : TimeProvider
    {
        private readonly CancellationTokenSource _cts;
        private int _calls;

        public CancellingTimeProvider(CancellationTokenSource cts) => _cts = cts;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                _cts.Cancel();
            }

            return DateTimeOffset.UnixEpoch;
        }
    }
}
