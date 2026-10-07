using System.Globalization;
using System.Text.Json;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One attempt GitHub made to deliver a webhook to us, as the App's delivery log
/// lists it. A redelivery is a new entry with its own <see cref="Id"/> and the
/// original's <see cref="Guid"/>, so the guid is what ties every attempt at one
/// event together.
/// </summary>
/// <param name="StatusCode">The HTTP status we answered with; 0 when GitHub got no answer at all (a timeout or a refused connection).</param>
public sealed record GitHubHookDelivery(
    long Id,
    string Guid,
    DateTime DeliveredAt,
    bool Redelivery,
    int StatusCode,
    string Event,
    string? Action)
{
    /// <summary>True when we answered with a 2xx, so GitHub's delivery reached the queue.</summary>
    public bool Succeeded => StatusCode is >= 200 and < 300;
}

/// <summary>
/// What a <c>pull_request</c> delivery was about, read back from the delivery log:
/// enough to ask GitHub whether that head is still the pull request's.
/// </summary>
public sealed record GitHubHookPullRequestDelivery(long InstallationId, string RepositoryFullName, int Number, string HeadSha);

/// <summary>A pull request as it stands now: whether it is open, and the commit its head points at.</summary>
public sealed record GitHubPullRequestHead(bool IsOpen, string HeadSha);

/// <summary>One page of the App's delivery log, newest first, with the cursor for the next (older) page.</summary>
public sealed record GitHubHookDeliveryPage(IReadOnlyList<GitHubHookDelivery> Deliveries, string? NextCursor);

/// <summary>
/// The webhook delivery log half of <see cref="GitHubAppClient"/> (#1121).
///
/// <para>GitHub does not resend a delivery we refused or never answered: it is
/// logged as failed and stays that way until somebody redelivers it. These two
/// calls are how the workbench does that itself. Both act as the App (the JWT),
/// because the delivery log belongs to the App's own webhook, not to any
/// installation, and need no permission an organisation grants.</para>
/// </summary>
public sealed partial class GitHubAppClient
{
    /// <summary>GitHub's page-size ceiling for the delivery log.</summary>
    internal const int DeliveriesPerPage = 100;

    /// <summary>
    /// Reads one page of the App's webhook delivery log, newest first.
    /// <paramref name="cursor"/> is the <see cref="GitHubHookDeliveryPage.NextCursor"/>
    /// of the previous page, or null for the newest.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No usable App registration on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused the call.</exception>
    public async Task<GitHubHookDeliveryPage> ListHookDeliveriesAsync(string? cursor, CancellationToken ct = default)
    {
        var jwt = await CreateAppJwtAsync(ct);
        var path = $"app/hook/deliveries?per_page={DeliveriesPerPage}";
        if (!string.IsNullOrEmpty(cursor)) path += "&cursor=" + Uri.EscapeDataString(cursor);

        using var request = NewRequest(HttpMethod.Get, path, jwt);
        using var response = await SendRawAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (message, documentationUrl) = ReadError(body);
            _logger.LogWarning(
                "GitHub refused to list webhook deliveries with {Status}: {Message}", (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, documentationUrl);
        }

        var deliveries = new List<GitHubHookDelivery>();
        using (var document = ParseOrThrow(body, path))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (ReadDelivery(item) is { } delivery) deliveries.Add(delivery);
                }
            }
        }

        var next = response.Headers.TryGetValues("Link", out var links)
            ? NextCursor(string.Join(',', links))
            : null;
        return new GitHubHookDeliveryPage(deliveries, next);
    }

    /// <summary>
    /// Asks GitHub to send delivery <paramref name="deliveryId"/> again. The new
    /// attempt carries the same event, payload and <c>X-GitHub-Delivery</c> guid,
    /// and shows up in the log as a new entry marked as a redelivery.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No usable App registration on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused (a delivery older than GitHub keeps, for one).</exception>
    public async Task RedeliverHookDeliveryAsync(long deliveryId, CancellationToken ct = default)
    {
        var jwt = await CreateAppJwtAsync(ct);
        using var request = NewRequest(
            HttpMethod.Post, $"app/hook/deliveries/{deliveryId.ToString(CultureInfo.InvariantCulture)}/attempts", jwt);
        using var response = await SendRawAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var (message, documentationUrl) = ReadError(body);
            _logger.LogWarning(
                "GitHub refused to redeliver webhook delivery {DeliveryId} with {Status}: {Message}",
                deliveryId, (int)response.StatusCode, message);
            throw new GitHubApiException(response.StatusCode, message, documentationUrl);
        }
    }

    /// <summary>
    /// Reads back which pull request and head a logged <c>pull_request</c>
    /// delivery carried, or null when the entry is gone or its payload is not one.
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">No usable App registration on this deployment.</exception>
    /// <exception cref="GitHubApiException">GitHub refused the call.</exception>
    public async Task<GitHubHookPullRequestDelivery?> GetHookPullRequestDeliveryAsync(
        long deliveryId, CancellationToken ct = default)
    {
        var jwt = await CreateAppJwtAsync(ct);
        using var request = NewRequest(
            HttpMethod.Get, $"app/hook/deliveries/{deliveryId.ToString(CultureInfo.InvariantCulture)}", jwt);
        using var document = await SendOrNotFoundAsync(request, ct);
        if (document is null) return null;

        var root = document.RootElement;
        if (!root.TryGetProperty("request", out var sent) || sent.ValueKind != JsonValueKind.Object
            || !sent.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!payload.TryGetProperty("installation", out var installation)
            || !installation.TryGetProperty("id", out var installationId)
            || !installationId.TryGetInt64(out var installationIdValue)
            || !payload.TryGetProperty("repository", out var repository)
            || !repository.TryGetProperty("full_name", out var fullName) || fullName.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("pull_request", out var pullRequest)
            || !pullRequest.TryGetProperty("number", out var number) || !number.TryGetInt32(out var numberValue)
            || !pullRequest.TryGetProperty("head", out var head)
            || !head.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return new GitHubHookPullRequestDelivery(installationIdValue, fullName.GetString()!, numberValue, sha.GetString()!);
    }

    /// <summary>
    /// Where pull request <paramref name="number"/> stands now, or null when GitHub
    /// has no such pull request for this credential.
    /// </summary>
    /// <exception cref="GitHubApiException">GitHub refused the call.</exception>
    public async Task<GitHubPullRequestHead?> GetPullRequestHeadAsync(
        string credential, string owner, string repo, int number, CancellationToken ct = default)
    {
        using var request = NewRequest(
            HttpMethod.Get, $"{RepoPath(owner, repo)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}", credential);
        using var document = await SendOrNotFoundAsync(request, ct);
        if (document is null) return null;

        var root = document.RootElement;
        var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var headSha = root.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var sha)
                      && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null;
        return headSha is null
            ? null
            : new GitHubPullRequestHead(string.Equals(state, "open", StringComparison.OrdinalIgnoreCase), headSha);
    }

    private static GitHubHookDelivery? ReadDelivery(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue)) return null;
        var guid = item.TryGetProperty("guid", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
        var stamp = item.TryGetProperty("delivered_at", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        if (guid is null || stamp is null
            || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var deliveredAt))
        {
            return null;
        }

        return new GitHubHookDelivery(
            Id: idValue,
            Guid: guid,
            DeliveredAt: deliveredAt.UtcDateTime,
            Redelivery: item.TryGetProperty("redelivery", out var r) && r.ValueKind == JsonValueKind.True,
            StatusCode: item.TryGetProperty("status_code", out var s) && s.TryGetInt32(out var status) ? status : 0,
            Event: item.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty,
            Action: item.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null);
    }

    /// <summary>
    /// The <c>cursor</c> of the <c>rel="next"</c> link in GitHub's <c>Link</c>
    /// header, or null on the last page. The delivery log pages by cursor rather
    /// than by page number.
    /// </summary>
    internal static string? NextCursor(string linkHeader)
    {
        foreach (var part in linkHeader.Split(','))
        {
            if (!part.Contains("rel=\"next\"", StringComparison.Ordinal)) continue;
            var open = part.IndexOf('<');
            var close = part.IndexOf('>');
            if (open < 0 || close <= open) continue;
            if (!Uri.TryCreate(part[(open + 1)..close], UriKind.Absolute, out var uri)) continue;
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == "cursor") return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return null;
    }
}
