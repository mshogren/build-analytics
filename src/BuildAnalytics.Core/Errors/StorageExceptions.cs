namespace BuildAnalytics.Core.Errors;

/// <summary>Another writer already holds the single-writer lock for the output root.</summary>
public sealed class OutputRootInUseException : Exception
{
    public OutputRootInUseException()
        : base("The output root is already in use by another writer.")
    {
    }

    public OutputRootInUseException(Exception? innerException)
        : base("The output root is already in use by another writer.", innerException)
    {
    }
}

/// <summary>
/// A valid artifact declared a schema version this build does not support.
/// This is not corruption and is never quarantined.
/// </summary>
public sealed class UnsupportedSchemaVersionException : Exception
{
    public UnsupportedSchemaVersionException(int expected, int actual)
        : base($"Schema version {actual} is not supported (expected {expected}); use a new output root.")
    {
        Expected = expected;
        Actual = actual;
    }

    public int Expected { get; }

    public int Actual { get; }
}

/// <summary>Existing run files belong to a different query fingerprint.</summary>
public sealed class FingerprintMismatchException : Exception
{
    public FingerprintMismatchException(string expected, string actual)
        : base("The existing run files belong to a different query; use a new output root.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
}

/// <summary>A run artifact exists but cannot be parsed.</summary>
public sealed class CorruptRunFileException : Exception
{
    public CorruptRunFileException(int runId)
        : base($"Run {runId} has a corrupt payload.")
    {
        RunId = runId;
    }

    public CorruptRunFileException(int runId, Exception? innerException)
        : base($"Run {runId} has a corrupt payload.", innerException)
    {
        RunId = runId;
    }

    public int RunId { get; }
}

/// <summary>An unexpected storage IO failure (not lock contention, not corruption).</summary>
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
