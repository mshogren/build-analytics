namespace BuildAnalytics.App.Storage;

/// <summary>
/// Central file-read helpers. Reads share <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>
/// so a concurrent writer's atomic <c>File.Move(overwrite: true)</c> is not blocked on Windows.
/// </summary>
public static class StorageFileAccess
{
    public static FileShare ReadShare => FileShare.ReadWrite | FileShare.Delete;

    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            ReadShare,
            bufferSize: 4096,
            useAsync: true);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
