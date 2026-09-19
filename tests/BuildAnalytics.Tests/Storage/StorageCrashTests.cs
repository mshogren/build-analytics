using System.Text.Json;
using BuildAnalytics.App.Storage;
using BuildAnalytics.Core;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

/// <summary>
/// Cross-store durability invariants: the checkpoint (manifest) is only ever behind the
/// durable run log, never ahead (ADR-109).
/// </summary>
public sealed class StorageCrashTests
{
    [Fact]
    public async Task Crash_after_durable_append_before_manifest_commit_preserves_runs()
    {
        using var root = new TempOutputRoot();
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected crash before manifest commit")
                : null);

        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        var manifestStore = new FileManifestStore(root.Path, failing);

        await runStore.AppendAsync([TestRuns.Create(id: 11)], CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(), CancellationToken.None));
        manifestStore.Dispose();

        var reopenedRuns = new FileRunStore(root.Path, new PhysicalFileOperations());
        using var reopenedManifest = new FileManifestStore(root.Path, new PhysicalFileOperations());

        Assert.Equal([11], (await reopenedRuns.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
        Assert.Null(await reopenedManifest.TryReadAsync(CancellationToken.None));
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

        await Assert.ThrowsAsync<IOException>(
            () => new FileRunStore(root.Path, failing).ReplaceAllAsync([TestRuns.Create(id: 2)], CancellationToken.None));

        Assert.Equal([1], (await store.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
        Assert.DoesNotContain(Directory.GetFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
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

        await runStore.AppendAsync([TestRuns.Create(id: 11)], CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(), CancellationToken.None));

        Assert.Equal([11], (await runStore.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
        Assert.Null(await manifestStore.TryReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fault_AfterFirstRunBeforeSecond_LeavesManifestNotCompletedAndRunsIntact()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        await runStore.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        using var manifestStore = new FileManifestStore(root.Path, new PhysicalFileOperations());
        await manifestStore.CommitAsync(SampleManifest(), CancellationToken.None);

        var failingRuns = new FileRunStore(
            root.Path,
            new RecordingFileOperations(
                new PhysicalFileOperations(),
                (operation, _) => operation == FileOperation.Append ? new IOException("injected") : null));
        await Assert.ThrowsAsync<IOException>(() => failingRuns.AppendAsync([TestRuns.Create(id: 2)], CancellationToken.None));

        Assert.Equal([1], (await runStore.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
        Assert.Equal(ManifestStatus.InProgress, (await manifestStore.TryReadAsync(CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Fault_BeforeManifestCommit_LeavesManifestNotCompletedAndRunsIntact()
    {
        using var root = new TempOutputRoot();
        var runStore = new FileRunStore(root.Path, new PhysicalFileOperations());
        await runStore.AppendAsync([TestRuns.Create(id: 1)], CancellationToken.None);

        var failNext = false;
        var failing = new RecordingFileOperations(
            new PhysicalFileOperations(),
            (operation, path) => failNext && operation == FileOperation.Rename && path.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new IOException("injected")
                : null);
        using var manifestStore = new FileManifestStore(root.Path, failing);
        await manifestStore.CommitAsync(SampleManifest(), CancellationToken.None);

        await runStore.AppendAsync([TestRuns.Create(id: 2)], CancellationToken.None);

        failNext = true;
        await Assert.ThrowsAsync<IOException>(() => manifestStore.CommitAsync(SampleManifest(ManifestStatus.Completed), CancellationToken.None));

        Assert.Equal([1, 2], (await runStore.ReadAllAsync(CancellationToken.None)).Runs.Select(run => run.Id));
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
