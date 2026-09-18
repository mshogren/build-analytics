using BuildAnalytics.App.Storage;

namespace BuildAnalytics.Tests.Storage;

internal enum FileOperation
{
    WriteTemp,
    FlushToDisk,
    Rename
}

/// <summary>
/// Wraps a real <see cref="IFileOperations"/> and records calls. An optional failure
/// factory throws deterministically between the atomic-write stages; an optional
/// delegate can block (F10 seam).
/// </summary>
internal sealed class RecordingFileOperations : IFileOperations
{
    private readonly IFileOperations _inner;
    private readonly Func<FileOperation, string, Exception?>? _failure;
    private readonly Action<FileOperation, string>? _beforeDelegate;

    public RecordingFileOperations(
        IFileOperations inner,
        Func<FileOperation, string, Exception?>? failure = null,
        Action<FileOperation, string>? beforeDelegate = null)
    {
        _inner = inner;
        _failure = failure;
        _beforeDelegate = beforeDelegate;
    }

    public List<(FileOperation Operation, string Path)> Calls { get; } = [];

    public Task WriteTempAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Record(FileOperation.WriteTemp, path);
        return _inner.WriteTempAsync(path, content, cancellationToken);
    }

    public Task FlushToDiskAsync(string path, CancellationToken cancellationToken)
    {
        Record(FileOperation.FlushToDisk, path);
        return _inner.FlushToDiskAsync(path, cancellationToken);
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Record(FileOperation.Rename, destinationPath);
        return _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
    }

    private void Record(FileOperation operation, string path)
    {
        Calls.Add((operation, path));

        _beforeDelegate?.Invoke(operation, path);

        var failure = _failure?.Invoke(operation, path);
        if (failure is not null)
        {
            throw failure;
        }
    }
}
