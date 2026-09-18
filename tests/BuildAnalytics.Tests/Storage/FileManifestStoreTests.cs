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

    private static Manifest SampleManifest()
        => new(
            Manifest.CurrentSchemaVersion,
            "fp-1",
            ManifestStatus.InProgress,
            "cursor-1",
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
}
