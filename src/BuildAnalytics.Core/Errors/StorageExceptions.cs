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

    /// <summary>Aggregate form: a log reported one or more entries with an unsupported version.</summary>
    public UnsupportedSchemaVersionException(int expected)
        : base($"One or more stored runs declared a schema version other than {expected}; use a new output root.")
    {
        Expected = expected;
        Actual = -1;
    }

    public int Expected { get; }

    public int Actual { get; }
}

/// <summary>Existing runs belong to a different query fingerprint.</summary>
public sealed class FingerprintMismatchException : Exception
{
    public FingerprintMismatchException(string expected, string actual)
        : base("The existing runs belong to a different query; use a new output root.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
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
