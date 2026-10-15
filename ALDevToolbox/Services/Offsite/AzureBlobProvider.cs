using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Offsite;

/// <summary>
/// Azure Blob Storage <see cref="IOffsiteStorageProvider"/> backed by
/// <c>Azure.Storage.Blobs</c>. Authenticates with a storage account name +
/// account key (carried in the same encrypted access/secret columns the S3
/// path uses), targets a single container (the <c>Bucket</c> field), and
/// names blobs identically to the S3 layout so the catalogue parsing in
/// <see cref="OffsiteBackupService"/> is shared.
///
/// <para>
/// Azure metadata names must be valid C# identifiers, so the hyphenated
/// <c>deployment-id</c> the service stamps is mapped to <c>deploymentid</c> on
/// write and back on read (see <see cref="MapMetadataToAzure"/> /
/// <see cref="MapMetadataFromAzure"/>) — keeping the deployment-fingerprint
/// guard working without the service knowing the backend differs.
/// </para>
/// </summary>
public sealed class AzureBlobProvider : IOffsiteStorageProvider
{
    /// <summary>
    /// Canonical, hyphenated metadata key the service uses on every backend
    /// (matches <see cref="OffsiteBackupService.DeploymentMetadataKey"/>).
    /// </summary>
    public const string DeploymentMetadataCanonical = "deployment-id";

    /// <summary>Identifier-legal form stored on Azure blobs.</summary>
    public const string DeploymentMetadataAzure = "deploymentid";

    private readonly ResolvedOffsiteSettings _settings;
    private readonly ILogger<AzureBlobProvider> _logger;
    private readonly BlobContainerClient _container;

    public AzureBlobProvider(ResolvedOffsiteSettings settings, ILogger<AzureBlobProvider> logger)
    {
        _settings = settings;
        _logger = logger;

        // AccessKey = storage account name, SecretKey = account key (see
        // SystemSettings.OffsiteProvider). Endpoint overrides the default host
        // for sovereign clouds and the Azurite emulator.
        var credential = new StorageSharedKeyCredential(settings.AccessKey, settings.SecretKey);
        var serviceUri = !string.IsNullOrWhiteSpace(settings.Endpoint)
            ? new Uri(settings.Endpoint)
            : new Uri($"https://{settings.AccessKey}.blob.core.windows.net");
        _container = new BlobServiceClient(serviceUri, credential).GetBlobContainerClient(settings.Bucket);
    }

    public async Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await _container.GetPropertiesAsync(cancellationToken: ct);
            return new OffsiteTestResult(true, $"Connected to container '{_settings.Bucket}'.");
        }
        catch (RequestFailedException ex)
        {
            return new OffsiteTestResult(false, $"Azure error: {ex.Status} {ex.ErrorCode} — {ex.Message}");
        }
        catch (Exception ex)
        {
            return new OffsiteTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task UploadAsync(string key, Stream content, string contentType,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);
        // The BlobUploadOptions overload overwrites an existing blob by default
        // (it sets no If-None-Match condition) — matching the S3 path and the
        // IOffsiteStorageProvider "overwriting any existing object" contract a
        // re-upload of the same key relies on. Do NOT switch to the
        // UploadAsync(Stream) / UploadAsync(Stream, ct) convenience overloads:
        // those set overwrite:false and throw 409 if the object already exists. #407
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Metadata = MapMetadataToAzure(metadata),
        }, ct);
    }

    public async Task<OffsiteDownload> OpenReadAsync(string key, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(key);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        var result = response.Value;
        var metadata = MapMetadataFromAzure(result.Details.Metadata);
        var length = result.Details.ContentLength > 0 ? result.Details.ContentLength : (long?)null;
        // Disposing the content stream releases the connection; OffsiteDownload
        // owns that disposal via its IAsyncDisposable.
        return new OffsiteDownload(result.Content, metadata, length);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key, CancellationToken ct)
    {
        var properties = await _container.GetBlobClient(key).GetPropertiesAsync(cancellationToken: ct);
        return MapMetadataFromAzure(properties.Value.Metadata);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct) =>
        // The SDK maps a 404 to false and throws for anything else.
        (await _container.GetBlobClient(key).ExistsAsync(ct)).Value;

    public async Task<IReadOnlyList<OffsiteStorageObject>> ListAsync(string prefix, bool topLevelOnly, CancellationToken ct)
    {
        var results = new List<OffsiteStorageObject>();
        if (topLevelOnly)
        {
            // The hierarchy listing returns the blobs directly under the prefix
            // and one virtual-directory item per deeper "/", which is skipped.
            await foreach (var item in _container.GetBlobsByHierarchyAsync(
                traits: BlobTraits.None, states: BlobStates.None, delimiter: "/", prefix: prefix, cancellationToken: ct))
            {
                if (!item.IsBlob) continue;
                results.Add(new OffsiteStorageObject(
                    item.Blob.Name,
                    item.Blob.Properties.ContentLength ?? 0,
                    (item.Blob.Properties.LastModified ?? default).UtcDateTime));
            }
            return results;
        }
        await foreach (var item in _container.GetBlobsAsync(
            traits: BlobTraits.None, states: BlobStates.None, prefix: prefix, cancellationToken: ct))
        {
            results.Add(new OffsiteStorageObject(
                item.Name,
                item.Properties.ContentLength ?? 0,
                (item.Properties.LastModified ?? default).UtcDateTime));
        }
        return results;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        // Azure has no native multi-blob delete without the extra Batch package;
        // per-blob delete is behaviourally identical for prune volumes.
        foreach (var key in keys)
        {
            await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    /// <summary>
    /// Maps the service's canonical (possibly hyphenated) metadata keys to the
    /// identifier-legal names Azure requires. Only the known
    /// <see cref="DeploymentMetadataCanonical"/> key is remapped; everything
    /// else passes through unchanged.
    /// </summary>
    public static IDictionary<string, string> MapMetadataToAzure(IReadOnlyDictionary<string, string> metadata)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            var key = k == DeploymentMetadataCanonical ? DeploymentMetadataAzure : k;
            mapped[key] = v;
        }
        return mapped;
    }

    /// <summary>
    /// Reverses <see cref="MapMetadataToAzure"/> so callers read the canonical
    /// key (e.g. <see cref="DeploymentMetadataCanonical"/>) regardless of the
    /// backend the object was written to.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MapMetadataFromAzure(IDictionary<string, string> metadata)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            var key = k == DeploymentMetadataAzure ? DeploymentMetadataCanonical : k;
            mapped[key] = v;
        }
        return mapped;
    }
}
