namespace BuildAnalytics.App.Storage;

/// <summary>
/// File-system seam for the atomic-write protocol. Tests inject failures between
/// stages to simulate crashes deterministically (F10).
/// </summary>
public interface IFileOperations
{
    Task WriteTempAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task FlushToDiskAsync(string path, CancellationToken cancellationToken);

    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="content"/> to <paramref name="path"/> (creating it if absent)
    /// and flushes to disk before returning (ADR-109 durable append seam).
    /// </summary>
    Task AppendAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}
