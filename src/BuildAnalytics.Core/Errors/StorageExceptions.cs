namespace BuildAnalytics.Core.Errors;

/// <summary>An unexpected storage IO failure (not corruption).</summary>
public sealed class StorageException : Exception
{
    public StorageException(string message)
        : base(message)
    {
    }

    public StorageException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
