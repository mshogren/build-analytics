namespace BuildAnalytics.App.Storage;

/// <summary>
/// Writes bytes with the temp-write -> flush-to-disk -> atomic-rename protocol.
/// The temp file lives in the destination directory so the rename stays atomic.
/// </summary>
public sealed class AtomicFileWriter
{
    private readonly IFileOperations _fileOperations;

    public AtomicFileWriter(IFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(fileOperations);
        _fileOperations = fileOperations;
    }

    public async Task WriteAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new InvalidOperationException("Destination path has no directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await _fileOperations.WriteTempAsync(tempPath, content, cancellationToken);
            await _fileOperations.FlushToDiskAsync(tempPath, cancellationToken);
            await _fileOperations.RenameAsync(tempPath, destinationPath, cancellationToken);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
