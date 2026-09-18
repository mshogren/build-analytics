using BuildAnalytics.App.Storage;
using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Tests.Storage;

public sealed class ManifestCompatibilityTests
{
    [Fact]
    public void Fingerprint_mismatch_with_non_empty_runs_throws()
    {
        var exception = Assert.Throws<FingerprintMismatchException>(
            () => ManifestCompatibility.EnsureCompatible(ManifestWith("actual"), "expected", [1]));

        Assert.Equal("expected", exception.Expected);
        Assert.Equal("actual", exception.Actual);
    }

    [Fact]
    public void Fingerprint_mismatch_with_empty_runs_is_allowed()
    {
        ManifestCompatibility.EnsureCompatible(ManifestWith("actual"), "expected", []);
    }

    [Fact]
    public void Matching_fingerprint_is_allowed()
    {
        ManifestCompatibility.EnsureCompatible(ManifestWith("same"), "same", [1]);
    }

    [Fact]
    public void Fingerprint_error_message_does_not_leak_paths()
    {
        var exception = Assert.Throws<FingerprintMismatchException>(
            () => ManifestCompatibility.EnsureCompatible(ManifestWith("actual"), "expected", [1]));

        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCompatible_NullArgs_Throw()
    {
        var manifest = ManifestWith("a");

        Assert.Throws<ArgumentNullException>(() => ManifestCompatibility.EnsureCompatible(null!, "a", []));
        Assert.Throws<ArgumentNullException>(() => ManifestCompatibility.EnsureCompatible(manifest, "a", null!));
    }

    private static Manifest ManifestWith(string fingerprint)
        => new(
            Manifest.CurrentSchemaVersion,
            fingerprint,
            ManifestStatus.InProgress,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            []);
}
