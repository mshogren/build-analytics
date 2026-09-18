using BuildAnalytics.App.Storage;

namespace BuildAnalytics.Tests.Storage;

public sealed class StorageFileAccessTests
{
    [Fact]
    public void ReadShare_is_readwrite_and_delete_not_read_only()
    {
        Assert.Equal(FileShare.ReadWrite | FileShare.Delete, StorageFileAccess.ReadShare);
        Assert.NotEqual(FileShare.Read, StorageFileAccess.ReadShare);
    }

    [Fact]
    public async Task ReadAllBytesAsync_returns_file_bytes()
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "payload.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], await StorageFileAccess.ReadAllBytesAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAllBytesAsync_honors_cancellation()
    {
        using var root = new TempOutputRoot();
        var path = Path.Combine(root.Path, "payload.bin");
        await File.WriteAllBytesAsync(path, [1], CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageFileAccess.ReadAllBytesAsync(path, cts.Token));
    }
}
