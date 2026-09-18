using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// Guards against resuming an output root that was written for a different query.
/// Slice 4 calls this before reusing an existing root.
/// </summary>
public static class ManifestCompatibility
{
    public static void EnsureCompatible(Manifest manifest, string expectedFingerprint, IReadOnlyCollection<int> existingRunIds)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(existingRunIds);

        if (existingRunIds.Count > 0 &&
            !string.Equals(manifest.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new FingerprintMismatchException(expectedFingerprint, manifest.Fingerprint);
        }
    }
}
