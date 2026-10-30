using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// What a repository read says about whether a module may be copied from it:
/// whether it is private, and its default branch.
/// </summary>
public sealed record GitHubRepositoryFacts(string FullName, bool IsPrivate, string DefaultBranch);

/// <summary>
/// The reads a module copied from a repository needs (#1415): what the repository
/// is, which commit a branch, tag or commit names, and the files at that commit as a
/// tarball. Every call takes a nullable credential, because a public repository
/// outside the connected organisation is read anonymously when the organisation
/// has no installation to read it with.
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>The one host a tarball redirect may point at.</summary>
    internal const string ArchiveHost = "codeload.github.com";

    /// <summary>
    /// How long a tarball may take to arrive. A module is a few megabytes at most,
    /// and an Editor is waiting on the save, so this is far shorter than the
    /// Release transfer deadline.
    /// </summary>
    internal static readonly TimeSpan ArchiveDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The repository, or null when GitHub says it does not exist or this
    /// credential cannot see it (404, and a 403/401 that is not a rate limit). A
    /// renamed repository answers 301 and is followed once, on the API host.
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub gave no answer: a rate limit, a server error, or no reply.</exception>
    public async Task<GitHubRepositoryFacts?> GetRepositorySummaryAsync(
        string? credential, string owner, string repo, CancellationToken ct = default)
    {
        using var request = NewOptionalCredentialRequest(HttpMethod.Get, RepoPath(owner, repo), credential);
        using var response = await SendRawAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.MovedPermanently
            && response.Headers.Location is { } location
            && location.IsAbsoluteUri
            && location.Scheme == Uri.UriSchemeHttps
            && string.Equals(location.Host, new Uri(ApiBaseUrl).Host, StringComparison.OrdinalIgnoreCase))
        {
            using var moved = NewOptionalCredentialRequest(HttpMethod.Get, location.ToString(), credential);
            using var movedResponse = await SendRawAsync(moved, ct);
            return await ReadRepositorySummaryAsync(movedResponse, moved, ct);
        }
        return await ReadRepositorySummaryAsync(response, request, ct);
    }

    private async Task<GitHubRepositoryFacts?> ReadRepositorySummaryAsync(
        HttpResponseMessage response, HttpRequestMessage request, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var document = ParseOrThrow(await response.Content.ReadAsStringAsync(ct), request.RequestUri?.ToString());
            var root = document.RootElement;
            var fullName = root.TryGetProperty("full_name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
            var isPrivate = !root.TryGetProperty("private", out var priv) || priv.ValueKind != JsonValueKind.False;
            var defaultBranch = root.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "main" : "main";
            return new GitHubRepositoryFacts(fullName, isPrivate, defaultBranch);
        }
        await ThrowIfNoVerdictAsync(response, request, ct);
        return null;
    }

    /// <summary>
    /// The full commit SHA <paramref name="gitRef"/> names (a branch, a tag or a
    /// commit; null means the default branch), or null when GitHub knows no such
    /// ref (404 or 422).
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused or gave no answer.</exception>
    public async Task<string?> ResolveCommitAsync(
        string? credential, string owner, string repo, string? gitRef, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim();
        using var request = NewOptionalCredentialRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/commits/{EscapePath(target)}", credential);
        // The SHA alone: GitHub answers this media type with the bare 40 characters
        // instead of a commit object listing every changed file.
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));
        using var response = await SendRawAsync(request, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (!IsCommitSha(body))
            {
                throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub did not say which commit that is.");
            }
            return body.ToLowerInvariant();
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            return null;
        }
        var (message, documentationUrl) = ReadError(body);
        _logger.LogWarning(
            "GitHub refused to resolve {Ref} on {Owner}/{Repo} with {Status}: {Message}",
            target, owner, repo, (int)response.StatusCode, message);
        throw new GitHubApiException(response.StatusCode, message, documentationUrl);
    }

    /// <summary>
    /// Streams the gzipped tarball of <paramref name="owner"/>/<paramref name="repo"/>
    /// at <paramref name="commitSha"/> into <paramref name="read"/>, never holding
    /// the whole download in memory.
    ///
    /// <para>GitHub answers with a <c>302</c> to <c>codeload.github.com</c>, which is
    /// followed by hand and only to that host (the typed client does not follow
    /// redirects; see <c>GitHubRegistration</c>). The hop carries no Authorization
    /// header: for a private repository the address itself holds a short-lived
    /// token, and sending the installation token on as well would hand a credential
    /// to a host that did not ask for it.</para>
    ///
    /// <para><paramref name="maxBytes"/> caps the compressed bytes read; past it the
    /// stream throws <see cref="ArchiveTooLargeException"/>, so a huge repository
    /// stops costing bandwidth the moment it crosses the line.</para>
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused, or pointed somewhere other than its archive host.</exception>
    public async Task<T> ReadTarballAsync<T>(
        string? credential, string owner, string repo, string commitSha, long maxBytes,
        Func<Stream, CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        Uri location;
        using (var request = NewOptionalCredentialRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/tarball/{Uri.EscapeDataString(commitSha)}", credential))
        using (var response = await SendRawAsync(request, ct))
        {
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } hop)
            {
                var (message, documentationUrl) = ReadError(await response.Content.ReadAsStringAsync(ct));
                _logger.LogWarning(
                    "GitHub refused the tarball of {Owner}/{Repo} at {Commit} with {Status}: {Message}",
                    owner, repo, commitSha, (int)response.StatusCode, message);
                throw new GitHubApiException(
                    response.IsSuccessStatusCode ? HttpStatusCode.BadGateway : response.StatusCode, message, documentationUrl);
            }
            location = hop;
        }

        if (!location.IsAbsoluteUri
            || location.Scheme != Uri.UriSchemeHttps
            || !string.Equals(location.Host, ArchiveHost, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "GitHub pointed the tarball of {Owner}/{Repo} at {Host}, which is not its archive host; not following.",
                owner, repo, location.IsAbsoluteUri ? location.Host : "(relative)");
            throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub pointed the download somewhere unexpected.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ArchiveDeadline);
        try
        {
            using var follow = new HttpRequestMessage(HttpMethod.Get, location);
            using var archive = await _http.SendAsync(follow, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!archive.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The tarball of {Owner}/{Repo} at {Commit} could not be fetched: {Status}.",
                    owner, repo, commitSha, (int)archive.StatusCode);
                throw new GitHubApiException(archive.StatusCode, "The repository's files could not be downloaded from GitHub.");
            }
            if (archive.Content.Headers.ContentLength is long declared && declared > maxBytes)
            {
                throw new ArchiveTooLargeException(maxBytes);
            }
            await using var body = await archive.Content.ReadAsStreamAsync(cts.Token);
            await using var capped = new CappedReadStream(body, maxBytes);
            return await read(capped, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The tarball of {Owner}/{Repo} did not arrive within {Seconds}s.", owner, repo, ArchiveDeadline.TotalSeconds);
            throw new GitHubApiException(HttpStatusCode.RequestTimeout, "GitHub did not send the repository's files within the time allowed.");
        }
    }

    /// <summary>Whether <paramref name="value"/> is a full 40-character hexadecimal commit SHA.</summary>
    public static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// A request carrying <paramref name="credential"/> when there is one and no
    /// Authorization header at all when there is not - an anonymous read of a
    /// public repository, at GitHub's anonymous rate limit.
    /// </summary>
    private static HttpRequestMessage NewOptionalCredentialRequest(HttpMethod method, string path, string? credential) =>
        credential is null ? new HttpRequestMessage(method, path) : NewRequest(method, path, credential);
}

/// <summary>A download or an archive's contents went past the size the caller allowed.</summary>
public sealed class ArchiveTooLargeException : Exception
{
    public ArchiveTooLargeException(long limitBytes)
        : base($"The repository is larger than the {limitBytes / (1024 * 1024)} MB that can be copied.")
    {
        LimitBytes = limitBytes;
    }

    public long LimitBytes { get; }
}
