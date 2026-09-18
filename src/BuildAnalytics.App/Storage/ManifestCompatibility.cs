using BuildAnalytics.Core.Errors;
using BuildAnalytics.Core.Models;

namespace BuildAnalytics.App.Storage;

/// <summary>
/// Guards against resuming an output root that was written for a different query.
/// Slice 4 calls this before reusing an existing root.
/// </summary>
public static class ManifestCompatibility
{
    public static void EnsureCompatible(
        Manifest manifest,
        string expectedFingerprint,
        IReadOnlyCollection<int> existingRunIds,
        bool requireMatch = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(existingRunIds);

        // ADR-92: a completed root is bound to its query regardless of runs/. Otherwise
        // only a non-empty runs/ with a different fingerprint is a conflict (ADR-3/66).
        if ((requireMatch || existingRunIds.Count > 0) &&
            !string.Equals(manifest.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new FingerprintMismatchException(expectedFingerprint, manifest.Fingerprint);
        }
    }
}
