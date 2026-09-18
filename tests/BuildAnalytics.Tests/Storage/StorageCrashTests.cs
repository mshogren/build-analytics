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

    private static Manifest SampleManifest()
        => new(
            Manifest.CurrentSchemaVersion,
            "fp",
            ManifestStatus.InProgress,
            "cursor-1",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            [],
            [],
            []);
}
