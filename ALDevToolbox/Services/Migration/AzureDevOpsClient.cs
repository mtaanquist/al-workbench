using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALDevToolbox.Services.Migration;

/// <summary>One repository in an Azure DevOps project, as Migrate Repositories lists it.</summary>
/// <param name="Id">Azure DevOps's own id for the repository.</param>
/// <param name="Name">The repository's name.</param>
/// <param name="CloneUrl">
/// Where to clone it from. Built from the project and the name rather than taken
/// from the answer, so it always points at <see cref="AzureDevOpsProject.Host"/>
/// and never carries a user name.
/// </param>
/// <param name="WebUrl">The repository's page, for a link.</param>
/// <param name="DefaultBranch">The default branch without <c>refs/heads/</c>, or null for an empty repository.</param>
/// <param name="Size">Size in bytes as Azure DevOps reports it, history included.</param>
/// <param name="IsDisabled">True when the repository is switched off in Azure DevOps and cannot be cloned.</param>
public sealed record AzureDevOpsRepository(
    string Id,
    string Name,
    string CloneUrl,
    string WebUrl,
    string? DefaultBranch,
    long Size,
    bool IsDisabled);

/// <summary>Why Azure DevOps refused a call, in the terms the page answers with.</summary>
public enum AzureDevOpsFailure
{
    /// <summary>The token was refused, has expired, or lacks the Code (Read) scope.</summary>
    Unauthorized,

    /// <summary>The organisation or project does not exist, or the token cannot see it.</summary>
    NotFound,

    /// <summary>Anything else: Azure DevOps down, a timeout, an answer we could not read.</summary>
    Unavailable,
}

/// <summary>An Azure DevOps call that failed, with the reason the page should give.</summary>
public sealed class AzureDevOpsApiException : Exception
{
    public AzureDevOpsApiException(AzureDevOpsFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public AzureDevOpsFailure Failure { get; }
}

/// <summary>
/// The few Azure DevOps REST calls Migrate Repositories makes (issue #1699),
/// always with the acting Admin's own Azure DevOps token. A typed client on a
/// fixed host (<see cref="AzureDevOpsProject.Host"/>), registered with redirects
/// off: Azure DevOps answers a refused token with a redirect to its sign-in page
/// (or a 203 with that page as the body), and following it would turn "your token
/// was refused" into "the answer was not JSON".
/// </summary>
public sealed class AzureDevOpsClient
{
    /// <summary>The REST API version every call pins.</summary>
    public const string ApiVersion = "7.1";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ILogger<AzureDevOpsClient> _logger;

    public AzureDevOpsClient(HttpClient http, ILogger<AzureDevOpsClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// The repositories in <paramref name="project"/>, sorted by name.
    /// </summary>
    /// <exception cref="AzureDevOpsApiException">Azure DevOps refused or could not answer.</exception>
    public async Task<IReadOnlyList<AzureDevOpsRepository>> ListRepositoriesAsync(
        AzureDevOpsProject project, string token, CancellationToken ct = default)
    {
        var path = $"/{Uri.EscapeDataString(project.Organization)}/{Uri.EscapeDataString(project.Project)}"
            + $"/_apis/git/repositories?api-version={ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AzureDevOpsApiException(AzureDevOpsFailure.Unavailable,
                "Azure DevOps did not answer in time. Try again in a moment.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not reach Azure DevOps to list the repositories of {Project}.", project.DisplayName);
            throw new AzureDevOpsApiException(AzureDevOpsFailure.Unavailable,
                "Could not reach Azure DevOps. Try again in a moment.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            // 203 is Azure DevOps's sign-in page served in place of the answer; a
            // redirect is the same refusal sent elsewhere.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NonAuthoritativeInformation
                || status is >= 300 and < 400)
            {
                throw new AzureDevOpsApiException(AzureDevOpsFailure.Unauthorized,
                    "Azure DevOps refused your token. It may have expired, or it may not have the Code (Read) "
                    + "scope for this organisation. Add a new one under Account → Repository access.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AzureDevOpsApiException(AzureDevOpsFailure.NotFound,
                    $"Azure DevOps has no project \"{project.Project}\" in the organisation \"{project.Organization}\" "
                    + "that your token can see. Check the address.");
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure DevOps answered {Status} to listing the repositories of {Project}.",
                    status, project.DisplayName);
                throw new AzureDevOpsApiException(AzureDevOpsFailure.Unavailable,
                    $"Azure DevOps could not list the repositories (it answered {status}). Try again in a moment.");
            }

            RepositoryList? body;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                body = await JsonSerializer.DeserializeAsync<RepositoryList>(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Azure DevOps answered the repository list of {Project} with something that is not JSON.", project.DisplayName);
                throw new AzureDevOpsApiException(AzureDevOpsFailure.Unavailable,
                    "Azure DevOps answered with something unexpected. Try again in a moment.", ex);
            }

            return (body?.Value ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => ToRepository(project, r))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static AzureDevOpsRepository ToRepository(AzureDevOpsProject project, RepositoryItem item)
    {
        var url = $"{project.WebUrl}/_git/{Uri.EscapeDataString(item.Name!)}";
        const string headsPrefix = "refs/heads/";
        var branch = item.DefaultBranch is { } b && b.StartsWith(headsPrefix, StringComparison.Ordinal)
            ? b[headsPrefix.Length..]
            : null;
        return new AzureDevOpsRepository(item.Id!, item.Name!, url, url, branch, item.Size ?? 0, item.IsDisabled ?? false);
    }

    private sealed class RepositoryList
    {
        [JsonPropertyName("value")] public List<RepositoryItem>? Value { get; set; }
    }

    private sealed class RepositoryItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("defaultBranch")] public string? DefaultBranch { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("isDisabled")] public bool? IsDisabled { get; set; }
    }
}
