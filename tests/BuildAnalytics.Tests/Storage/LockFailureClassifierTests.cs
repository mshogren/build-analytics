using BuildAnalytics.Core.Errors;

namespace BuildAnalytics.Tests.Storage;

public sealed class LockFailureClassifierTests
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int UnixEagain = 11;
    private const int MacOsEwouldBlock = 35;

    [Theory]
    [InlineData(SharingViolation)]
    [InlineData(LockViolation)]
    [InlineData(UnixEagain)]
    [InlineData(MacOsEwouldBlock)]
    public void Contention_hresults_are_classified_as_contention(int hresult)
    {
        Assert.Equal(LockFailure.Contention, LockFailureClassifier.Classify(new FakeIoException(hresult)));
    }

    [Fact]
    public void Other_io_failures_and_null_are_storage_failures()
    {
        Assert.Equal(LockFailure.StorageFailure, LockFailureClassifier.Classify(new FakeIoException(5)));
        Assert.Equal(LockFailure.StorageFailure, LockFailureClassifier.Classify(new IOException("boom")));
        Assert.Equal(LockFailure.StorageFailure, LockFailureClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(LockFailure.StorageFailure, LockFailureClassifier.Classify(null));
    }

    private sealed class FakeIoException : IOException
    {
        public FakeIoException(int hresult) => HResult = hresult;
    }
}
