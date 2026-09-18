namespace BuildAnalytics.App.Storage;

/// <summary>Another writer already holds the single-writer lock for the output root.</summary>
public sealed class OutputRootInUseException : Exception
{
    public OutputRootInUseException()
        : base("The output root is already in use by another writer.")
    {
    }

    public OutputRootInUseException(Exception innerException)
        : base("The output root is already in use by another writer.", innerException)
    {
    }
}

/// <summary>The manifest on disk was written by an incompatible schema version.</summary>
public sealed class SchemaVersionMismatchException : Exception
{
    public SchemaVersionMismatchException(int expected, int actual)
        : base($"Manifest schema version {actual} is not supported (expected {expected}); use a new output root.")
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
