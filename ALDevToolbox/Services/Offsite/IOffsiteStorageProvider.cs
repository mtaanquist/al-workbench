using ALDevToolbox.Services.Backups;

namespace ALDevToolbox.Services.Offsite;

/// <summary>
/// One object as the storage backend reports it: the backend-native key, its
/// size in bytes, and the last-modified instant in UTC. The catalogue-shaped
/// records (<c>OffsiteObjectInfo</c> / <c>OffsitePerTenantObjectInfo</c>) that
/// the UI consumes are derived from these by <see cref="OffsiteBackupService"/>,
/// which owns the key parsing — providers stay agnostic about our naming.
/// </summary>
public sealed record OffsiteStorageObject(string Key, long Size, DateTime LastModifiedUtc);

/// <summary>
/// An opened object body plus its user metadata and (best-effort) content
/// length. The body is a live network stream; the caller reads it and disposes
/// this record (which disposes the stream). <see cref="ContentLength"/> is
/// <see langword="null"/> when the backend doesn't report it — the download
/// path tolerates that and skips the progress total.
/// </summary>
public sealed record OffsiteDownload(
    Stream Body,
    IReadOnlyDictionary<string, string> Metadata,
    long? ContentLength) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Body.DisposeAsync();
}

/// <summary>
/// The raw object-storage operations <see cref="OffsiteBackupService"/> needs,
/// abstracted over the concrete backend (S3-compatible or Azure Blob). Only
/// transport lives here — object-key building, <c>.partial</c> staging, the
/// deployment-id fingerprint gate, retention filtering, and DB bookkeeping all
/// stay in the service so both backends share that behaviour byte-for-byte.
///
/// <para>
/// Instances are constructed per call from the resolved (decrypted) settings via
/// <see cref="IOffsiteStorageProviderFactory"/> and are short-lived; an
/// implementation that holds an SDK client should implement
/// <see cref="IDisposable"/> so the service can dispose it in a <c>finally</c>.
/// </para>
/// </summary>
public interface IOffsiteStorageProvider
{
    /// <summary>Cheap reachability + auth check against the configured bucket/container.</summary>
    Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="content"/> to <paramref name="key"/> with the
    /// given user metadata, overwriting any existing object. The caller owns
    /// and disposes the stream. Implementations normalise metadata key casing
    /// to their backend's rules and reverse it on read.
    /// </summary>
    Task UploadAsync(string key, Stream content, string contentType,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct);

    /// <summary>
    /// Opens the object at <paramref name="key"/> for reading. The returned
    /// <see cref="OffsiteDownload"/> carries the body, the user metadata (keys
    /// mapped back to the canonical form the service expects), and the length
    /// when known. The caller disposes it.
    /// </summary>
    Task<OffsiteDownload> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>
    /// Reads the user metadata of the object at <paramref name="key"/> without
    /// its body, keys mapped back to the canonical form as for
    /// <see cref="OpenReadAsync"/>. The listing and the retention prune use it
    /// to read the deployment stamp, which no backend returns in a listing.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key, CancellationToken ct);

    /// <summary>
    /// Lists objects whose key starts with <paramref name="prefix"/>, newest
    /// first not required (the service sorts), capped at
    /// <paramref name="maxObjects"/>. Implementations page internally and stop
    /// at the cap so neither walks an unbounded bucket. With
    /// <paramref name="topLevelOnly"/> only the objects directly under
    /// <paramref name="prefix"/> are returned, not those under a further
    /// <c>/</c>; the whole-database listing uses that to step over the
    /// per-tenant subtree instead of paging through it.
    /// </summary>
    Task<IReadOnlyList<OffsiteStorageObject>> ListAsync(string prefix, int maxObjects, bool topLevelOnly, CancellationToken ct);

    /// <summary>Deletes the given keys. The provider chooses batch vs per-object.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct);
}
