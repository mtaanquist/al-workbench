using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Offsite;

/// <summary>
/// S3-compatible <see cref="IOffsiteStorageProvider"/> backed by
/// <c>AWSSDK.S3</c>. Works against AWS S3, MinIO, Cloudflare R2 and Backblaze
/// B2 (set <see cref="ResolvedOffsiteSettings.ForcePathStyle"/> for the
/// non-AWS servers). This is the lift of the original client code out of
/// <see cref="OffsiteBackupService"/>; behaviour is unchanged.
/// </summary>
public sealed class S3Provider : IOffsiteStorageProvider, IDisposable
{
    /// <summary>S3 batches up to 1000 keys per delete request.</summary>
    private const int DeleteBatchSize = 1000;

    private readonly ResolvedOffsiteSettings _settings;
    private readonly ILogger<S3Provider> _logger;
    private readonly IAmazonS3 _client;

    public S3Provider(ResolvedOffsiteSettings settings, ILogger<S3Provider> logger)
    {
        _settings = settings;
        _logger = logger;
        var credentials = new BasicAWSCredentials(settings.AccessKey, settings.SecretKey);
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
        };
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            config.ServiceURL = settings.Endpoint;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        _client = new AmazonS3Client(credentials, config);
    }

    public async Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await _client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _settings.Bucket }, ct);
            return new OffsiteTestResult(true, $"Connected to bucket '{_settings.Bucket}'.");
        }
        catch (AmazonS3Exception ex)
        {
            return new OffsiteTestResult(false, $"S3 error: {ex.ErrorCode} — {ex.Message}");
        }
        catch (Exception ex)
        {
            return new OffsiteTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task UploadAsync(string key, Stream content, string contentType,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
            // R2/B2/MinIO reject the streaming chunked signature; the path-style
            // toggle doubles as the "this isn't real AWS" signal we already used.
            DisablePayloadSigning = _settings.ForcePathStyle,
        };
        foreach (var (k, v) in metadata)
        {
            request.Metadata.Add(k, v);
        }
        await _client.PutObjectAsync(request, ct);
    }

    public async Task<OffsiteDownload> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
        }, ct);

        var metadata = CanonicalMetadata(response.Metadata);

        var length = response.ContentLength > 0 ? response.ContentLength : (long?)null;
        return new OffsiteDownload(response.ResponseStream, metadata, length);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key, CancellationToken ct)
    {
        var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _settings.Bucket,
            Key = key,
        }, ct);
        return CanonicalMetadata(response.Metadata);
    }

    private static Dictionary<string, string> CanonicalMetadata(MetadataCollection source)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var metaKey in source.Keys)
        {
            // The SDK exposes user metadata keys with the x-amz-meta- prefix;
            // strip it so callers look up the canonical name (e.g. "deployment-id").
            var canonical = metaKey.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? metaKey["x-amz-meta-".Length..]
                : metaKey;
            metadata[canonical] = source[metaKey];
        }
        return metadata;
    }

    public async Task<IReadOnlyList<OffsiteStorageObject>> ListAsync(string prefix, int maxObjects, bool topLevelOnly, CancellationToken ct)
    {
        if (maxObjects < 1) maxObjects = 1;
        var results = new List<OffsiteStorageObject>(capacity: Math.Min(maxObjects, 256));
        string? continuation = null;
        do
        {
            var resp = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _settings.Bucket,
                Prefix = prefix,
                // With a delimiter S3 rolls everything under a further "/" up
                // into CommonPrefixes, which are ignored here.
                Delimiter = topLevelOnly ? "/" : null,
                ContinuationToken = continuation,
                MaxKeys = Math.Min(1000, maxObjects - results.Count),
            }, ct);

            // AWS SDK v4 leaves response collections null rather than empty, so a
            // prefix with no objects yields a null S3Objects instead of an empty
            // list. Size and LastModified became nullable in the same change;
            // S3 always sends both for a real object, so the fallbacks are
            // belt-and-braces rather than a case we expect to hit.
            foreach (var obj in resp.S3Objects ?? [])
            {
                results.Add(new OffsiteStorageObject(
                    obj.Key,
                    obj.Size ?? 0,
                    obj.LastModified?.ToUniversalTime() ?? default));
                if (results.Count >= maxObjects) break;
            }

            continuation = resp.IsTruncated == true && results.Count < maxObjects
                ? resp.NextContinuationToken
                : null;
        } while (continuation is not null);

        return results;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return;
        foreach (var batch in keys.Chunk(DeleteBatchSize))
        {
            await _client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _settings.Bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
                Quiet = true,
            }, ct);
        }
    }

    public void Dispose() => _client.Dispose();
}
