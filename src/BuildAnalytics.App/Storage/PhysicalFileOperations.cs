namespace BuildAnalytics.App.Storage;

/// <summary>Real file-system implementation of the atomic-write seam.</summary>
public sealed class PhysicalFileOperations : IFileOperations
{
    public async Task WriteTempAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
    }

    public Task FlushToDiskAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // True power-loss fsync durability is out of unit-test scope; tests assert that
        // this stage is invoked on a non-empty temp file BEFORE the atomic rename.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Flush(flushToDisk: true);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        File.Move(sourcePath, destinationPath, overwrite: true);
        return Task.CompletedTask;
    }

    public async Task AppendAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
