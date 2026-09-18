using BuildAnalytics.Core.Models;

namespace BuildAnalytics.Core.Ports;

/// <summary>Reads and commits retrieval progress for one output root.</summary>
public interface IManifestStore
{
    Task<Manifest?> TryReadAsync(CancellationToken cancellationToken);

    Task CommitAsync(Manifest manifest, CancellationToken cancellationToken);
}
