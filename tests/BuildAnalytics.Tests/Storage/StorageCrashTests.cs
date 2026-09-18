using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

/// <summary>
/// Cross-store durability invariants: the checkpoint (manifest) is only ever behind the
/// durable run files, never ahead.
/// </summary>
public sealed class StorageCrashTests
{
    [Fact]
    public async Task Crash_between_run_write_and_manifest_commit_leaves_checkpoint_behind_files()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected crash before manifest commit")
                : null);

        var runStore = new FileRunStore(root.Path, failing);
        var manifestStore = new FileManifestStore(root.Path, failing);
        var run = TestRuns.Create(id: 11);

        await runStore.WriteAsync(run, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(), CancellationToken.None));
        manifestStore.Dispose();

        var reopenedRuns = new FileRunStore(root.Path, new PhysicalFileOperations());
        using var reopenedManifest = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Equal([11], await reopenedRuns.ListRunIdsAsync(CancellationToken.None));
        Assert.Null(await reopenedManifest.TryReadAsync(CancellationToken.None));

        // Replay upserts the run instead of duplicating it, then the checkpoint advances.
        await reopenedRuns.WriteAsync(run, CancellationToken.None);
        await reopenedManifest.CommitAsync(SampleManifest(), CancellationToken.None);

        Assert.Equal([11], await reopenedRuns.ListRunIdsAsync(CancellationToken.None));
        Assert.NotNull(await reopenedManifest.TryReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(FileOperation.WriteTemp)]
    [InlineData(FileOperation.FlushToDisk)]
    [InlineData(FileOperation.Rename)]
    public async Task Checkpoint_NeverAheadOfFiles_AfterEachCheckpoint(FileOperation failAt)
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == failAt && path.Contains("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);
        using var manifestStore = new FileManifestStore(root.Path, failing);
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());

        await runStore.WriteAsync(TestRuns.Create(id: 11), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(), CancellationToken.None));

        Assert.Equal([11], await runStore.ListRunIdsAsync(CancellationToken.None));
        Assert.Null(await manifestStore.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fault_AfterFirstRunBeforeSecond_LeavesManifestNotCompletedAndRunsIntact()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        await runStore.WriteAsync(TestRuns.Create(id: 1), CancellationToken.None);

        using var manifestStore = new FileManifestStore(root.Path, new PhysicalFileOperations());
        await manifestStore.CommitAsync(SampleManifest(), CancellationToken.None);

        var failingRuns = new FileRunStore(
            root.Path,
            new RecordingFileOperations(
                new PhysicalFileOperations(),
                (operation, _) => operation == FileOperation.WriteTemp ? new IOException("injected") : null));
        await Assert.ThrowsAsync<IOException>(() => failingRuns.WriteAsync(TestRuns.Create(id: 2), CancellationToken.None));

        Assert.Equal([1], await runStore.ListRunIdsAsync(CancellationToken.None));
        Assert.Equal(ManifestStatus.InProgress, (await manifestStore.TryReadAsync(CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Fault_BeforeManifestCommit_LeavesManifestNotCompletedAndRunsIntact()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        await runStore.WriteAsync(TestRuns.Create(id: 1), CancellationToken.None);

        var failNext = false;
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => failNext && operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);
        using var manifestStore = new FileManifestStore(root.Path, failing);
        await manifestStore.CommitAsync(SampleManifest(), CancellationToken.None);

        await runStore.WriteAsync(TestRuns.Create(id: 2), CancellationToken.None);

        failNext = true;
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(ManifestStatus.Completed), CancellationToken.None));

        Assert.Equal([1, 2], await runStore.ListRunIdsAsync(CancellationToken.None));
        Assert.Equal(ManifestStatus.InProgress, (await manifestStore.TryReadAsync(CancellationToken.None))!.Status);
    }

    private static Manifest SampleManifest(ManifestStatus status = ManifestStatus.InProgress)
        => new(
            Manifest.CurrentSchemaVersion,
            "fp",
            status,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            []);
}
