using BuildAnalytics.App.Storage;

namespace BuildAnalytics.Tests.Storage;

public enum FileOperation
{
    WriteTemp,
    FlushToDisk,
    Rename,
    Append
}

/// <summary>
/// Wraps a real <see cref="IFileOperations"/> and records calls. A failure factory throws
/// deterministically BEFORE delegating; an after-delegate runs AFTER the real operation
/// (used to simulate a crash that leaves the renamed target complete). F10 seam.
/// </summary>
internal sealed class RecordingFileOperations : IFileOperations
{
    private readonly IFileOperations _inner;
    private readonly Func<FileOperation, string, Exception?>? _failure;
    private readonly Action<FileOperation, string>? _beforeDelegate;
    private readonly Action<FileOperation, string>? _afterDelegate;

    public RecordingFileOperations(
        IFileOperations inner,
        Func<FileOperation, string, Exception?>? failure = null,
        Action<FileOperation, string>? beforeDelegate = null,
        Action<FileOperation, string>? afterDelegate = null)
    {
        _inner = inner;
        _failure = failure;
        _beforeDelegate = beforeDelegate;
        _afterDelegate = afterDelegate;
    }

    public List<(FileOperation Operation, string Path)> Calls { get; } = [];

    public async Task WriteTempAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Record(FileOperation.WriteTemp, path);
        await _inner.WriteTempAsync(path, content, cancellationToken);
        After(FileOperation.WriteTemp, path);
    }

    public async Task FlushToDiskAsync(string path, CancellationToken cancellationToken)
    {
        Record(FileOperation.FlushToDisk, path);
        await _inner.FlushToDiskAsync(path, cancellationToken);
        After(FileOperation.FlushToDisk, path);
    }

    public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Record(FileOperation.Rename, destinationPath);
        await _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
        After(FileOperation.Rename, destinationPath);
    }

    public async Task AppendAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        Record(FileOperation.Append, path);
        await _inner.AppendAsync(path, content, cancellationToken);
        After(FileOperation.Append, path);
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

    private void After(FileOperation operation, string path) => _afterDelegate?.Invoke(operation, path);
}
