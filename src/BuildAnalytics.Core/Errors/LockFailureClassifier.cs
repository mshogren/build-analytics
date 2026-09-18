namespace BuildAnalytics.Core.Errors;

public enum LockFailure
{
    /// <summary>Another process/stream already holds the exclusive lock.</summary>
    Contention,

    /// <summary>Any other IO problem (permissions, missing directory, disk error, ...).</summary>
    StorageFailure
}

/// <summary>
/// Pure classification of exceptions raised while acquiring the manifest lock.
/// Contention is a normal, typed outcome; everything else is an unexpected storage failure.
/// </summary>
public static class LockFailureClassifier
{
    private const int SharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION (Windows)
    private const int LockViolation = unchecked((int)0x80070021);    // ERROR_LOCK_VIOLATION (Windows)
    private const int Eagain = 11;                                   // EAGAIN / EWOULDBLOCK (Linux)
    private const int EwouldBlockMacOs = 35;                         // EWOULDBLOCK (macOS)

    public static LockFailure Classify(Exception? exception)
        => exception switch
        {
            null => LockFailure.StorageFailure,
            UnauthorizedAccessException => LockFailure.StorageFailure,
            IOException io when IsContention(io.HResult) => LockFailure.Contention,
            _ => LockFailure.StorageFailure
        };

    private static bool IsContention(int hresult)
        => hresult is SharingViolation or LockViolation or Eagain or EwouldBlockMacOs;
}
