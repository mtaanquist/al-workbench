using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One GitHub Release: the tag it is pinned to, its title and page, the assets
/// hanging off it, and the templated URL new assets are uploaded to.
///
/// <para><see cref="UploadUrl"/> comes back from GitHub as a URI template
/// (<c>...\/assets{?name,label}</c>) on a different host to the API - see
/// <see cref="GitHubAppClient.UploadReleaseAssetAsync"/>.</para>
/// </summary>
public sealed record GitHubRelease(
    long Id,
    string TagName,
    string? Name,
    DateTimeOffset? PublishedAt,
    string HtmlUrl,
    string UploadUrl,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>One file attached to a Release.</summary>
public sealed record GitHubReleaseAsset(long Id, string Name, long SizeBytes);

/// <summary>
/// The Releases half of the client (issue #632): publishing a build's
/// <c>.app</c> files to a repository's Releases page, and reading them back so
/// a version the workbench did not build can still be deployed.
///
/// <para>Every call here rides the <em>installation</em> token. A Release is an
/// act of the organisation, and the publish half runs inside a build worker
/// where there may be no user at all. See
/// <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>
    /// The Release at <paramref name="tag"/>, or <see langword="null"/> when the
    /// repository has none there. A missing Release is an ordinary answer: it is
    /// what tells the publisher to create one rather than replace its assets.
    /// </summary>
    public async Task<GitHubRelease?> GetReleaseByTagAsync(
        string credential, string owner, string repo, string tag, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/releases/tags/{EscapePath(tag)}", credential);
        using var document = await SendOrNotFoundAsync(request, ct);
        return document is null ? null : ReadRelease(document.RootElement);
    }

    /// <summary>
    /// The repository's Releases, newest first. One page of 30 - a release
    /// pipeline offers recent versions to deploy, and a list nobody would scroll
    /// past is not worth paging for.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(
        string credential, string owner, string repo, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/releases?per_page=30", credential);
        using var document = await SendAsync(request, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var releases = document.RootElement.EnumerateArray()
            .Select(ReadRelease)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        _logger.LogInformation(
            "GitHub reports {ReleaseCount} releases on {Owner}/{Repo}.", releases.Count, owner, repo);
        return releases;
    }

    /// <summary>
    /// Creates a Release at <paramref name="tag"/>, pointing the tag at
    /// <paramref name="targetCommit"/> - the commit that was built - or, when that is
    /// not known, at the repository's default branch. A tag on the default branch's
    /// head would name a commit the build never saw whenever it built another branch
    /// or GitHub has moved on since.
    ///
    /// <para><c>generate_release_notes</c> is off: the body says which apps this
    /// build produced and links back to the build, which is the thing a
    /// consultant opening the Releases page wants. GitHub's generated notes
    /// would describe commits nobody here chose.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused - typically a rule that restricts tag creation.</exception>
    public async Task<GitHubRelease> CreateReleaseAsync(
        string credential, string owner, string repo, string tag, string name, string body,
        string? targetCommit = null, CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Post, $"{RepoPath(owner, repo)}/releases", credential,
            new
            {
                tag_name = tag,
                // Left out when null, which makes GitHub use the default branch.
                target_commitish = string.IsNullOrWhiteSpace(targetCommit) ? null : targetCommit,
                name,
                body,
                draft = false,
                prerelease = false,
                generate_release_notes = false,
            });
        using var document = await SendAsync(request, ct);
        var release = ReadRelease(document.RootElement)
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say which release it created.");
        _logger.LogInformation("Created the GitHub release {Tag} on {Owner}/{Repo}.", tag, owner, repo);
        return release;
    }

    /// <summary>
    /// Rewrites an existing Release's title and body. The tag is never sent, so
    /// re-publishing a version can never move it onto a different commit.
    /// </summary>
    public async Task<GitHubRelease> UpdateReleaseAsync(
        string credential, string owner, string repo, long releaseId, string name, string body,
        CancellationToken ct = default)
    {
        using var request = NewJsonRequest(
            HttpMethod.Patch, $"{RepoPath(owner, repo)}/releases/{releaseId}", credential,
            new { name, body });
        using var document = await SendAsync(request, ct);
        var release = ReadRelease(document.RootElement)
            ?? throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say which release it updated.");
        _logger.LogInformation("Updated the GitHub release {ReleaseId} on {Owner}/{Repo}.", releaseId, owner, repo);
        return release;
    }

    /// <summary>
    /// Removes one asset from a Release. GitHub has no "replace an asset" call,
    /// so re-publishing a version deletes the old files before uploading the new
    /// ones. An asset that is already gone is not an error - the end state is
    /// what the caller wanted.
    /// </summary>
    public async Task DeleteReleaseAssetAsync(
        string credential, string owner, string repo, long assetId, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Delete, $"{RepoPath(owner, repo)}/releases/assets/{assetId}", credential);
        using var response = await SendRawAsync(request, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Removed release asset {AssetId} from {Owner}/{Repo}.", assetId, owner, repo);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var (message, url) = ReadError(body);
        _logger.LogWarning(
            "GitHub refused to delete release asset {AssetId} on {Owner}/{Repo} with {Status}: {Message}",
            assetId, owner, repo, (int)response.StatusCode, message);
        throw new GitHubApiException(response.StatusCode, message, url);
    }

    /// <summary>
    /// Attaches one file to a Release.
    ///
    /// <para>Uploads do not go to the API host: GitHub hands back an
    /// <c>upload_url</c> on <c>uploads.github.com</c> as a URI template, and the
    /// <c>{?name,label}</c> suffix has to be stripped and the name supplied as a
    /// query parameter. The request is addressed absolutely so the typed client's
    /// base address is bypassed, and the body goes up as raw bytes - a
    /// <c>.app</c> is a binary, not JSON.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the upload.</exception>
    public async Task<GitHubReleaseAsset> UploadReleaseAssetAsync(
        string credential, string uploadUrl, string fileName, byte[] content, CancellationToken ct = default)
    {
        // The upload host arrives in GitHub's own answer, so it is checked before
        // the installation token is attached to a request going there. A relative
        // or empty address would otherwise resolve against the client's base URI
        // and post an organisation's build - with its credential - somewhere
        // nobody chose.
        var target = $"{StripUriTemplate(uploadUrl)}?name={Uri.EscapeDataString(fileName)}";
        if (!IsGitHubUploadTarget(target))
        {
            _logger.LogWarning("Refused to upload {FileName}: GitHub named an upload address we do not trust.", fileName);
            throw new GitHubApiException(
                HttpStatusCode.BadGateway, "GitHub did not say where to upload the release file.");
        }

        using var request = NewRequest(HttpMethod.Post, target, credential);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendRawAsync(request, ct, TransferDeadline);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (message, url) = ReadError(body);
            _logger.LogWarning(
                "GitHub refused the release asset {FileName} with {Status}: {Message}",
                fileName, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, url);
        }

        using var document = ParseOrThrow(body, target);
        var asset = ReadAsset(document.RootElement)
            ?? new GitHubReleaseAsset(0, fileName, content.LongLength);
        _logger.LogInformation(
            "Uploaded the release asset {FileName} ({SizeBytes} bytes).", fileName, content.LongLength);
        return asset;
    }

    /// <summary>
    /// One Release asset's bytes.
    ///
    /// <para>GitHub answers this route with a <c>302</c> to blob storage rather
    /// than with the file, and the typed client does not follow redirects on
    /// purpose (see <c>GitHubRegistration</c>). So the <c>Location</c> is read
    /// and fetched by hand - <strong>without the Authorization header</strong>.
    /// The storage host has its own signed URL, and sending it the installation
    /// token would hand a credential to a service that never asked for one; some
    /// of them refuse the request outright for carrying two.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused, or pointed somewhere we could not read.</exception>
    public async Task<byte[]> DownloadReleaseAssetAsync(
        string credential, string owner, string repo, long assetId, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/releases/assets/{assetId}", credential);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await SendRawAsync(request, ct, TransferDeadline);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
        {
            using var follow = new HttpRequestMessage(HttpMethod.Get, location);
            follow.Headers.Accept.Clear();
            follow.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var stored = await SendRawAsync(follow, ct, TransferDeadline);
            if (!stored.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Release asset {AssetId} on {Owner}/{Repo} could not be fetched from storage: {Status}.",
                    assetId, owner, repo, (int)stored.StatusCode);
                throw new GitHubApiException(stored.StatusCode, "The release file could not be downloaded from GitHub.");
            }
            var bytes = await stored.Content.ReadAsByteArrayAsync(ct);
            _logger.LogInformation(
                "Downloaded release asset {AssetId} from {Owner}/{Repo} ({SizeBytes} bytes).",
                assetId, owner, repo, bytes.LongLength);
            return bytes;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct);
        var (errorMessage, documentationUrl) = ReadError(errorBody);
        _logger.LogWarning(
            "GitHub refused release asset {AssetId} on {Owner}/{Repo} with {Status}: {Message}",
            assetId, owner, repo, (int)response.StatusCode, errorMessage);
        throw new GitHubApiException(response.StatusCode, errorMessage, documentationUrl);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Found or HttpStatusCode.MovedPermanently
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.SeeOther
            or HttpStatusCode.PermanentRedirect;

    /// <summary>Drops the <c>{?name,label}</c> URI-template suffix GitHub appends to <c>upload_url</c>.</summary>
    private static string StripUriTemplate(string url)
    {
        var brace = url.IndexOf('{');
        return brace < 0 ? url : url[..brace];
    }

    /// <summary>
    /// Whether <paramref name="target"/> is an absolute https address on GitHub's
    /// own upload host. An empty <c>upload_url</c> strips to nothing, which would
    /// otherwise be a relative URI resolved against <c>api.github.com</c>.
    /// </summary>
    private static bool IsGitHubUploadTarget(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "uploads.github.com" || host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal);
    }

    private static GitHubRelease? ReadRelease(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("id", out var id) || !id.TryGetInt64(out var releaseId)) return null;

        var assets = new List<GitHubReleaseAsset>();
        if (element.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (ReadAsset(entry) is { } asset) assets.Add(asset);
            }
        }

        return new GitHubRelease(
            Id: releaseId,
            TagName: Text(element, "tag_name") ?? string.Empty,
            Name: Text(element, "name"),
            PublishedAt: element.TryGetProperty("published_at", out var published)
                && published.TryGetDateTimeOffset(out var at) ? at : null,
            HtmlUrl: Text(element, "html_url") ?? string.Empty,
            UploadUrl: Text(element, "upload_url") ?? string.Empty,
            Assets: assets);
    }

    private static GitHubReleaseAsset? ReadAsset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("id", out var id) || !id.TryGetInt64(out var assetId)) return null;
        var size = element.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        return new GitHubReleaseAsset(assetId, Text(element, "name") ?? string.Empty, size);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
