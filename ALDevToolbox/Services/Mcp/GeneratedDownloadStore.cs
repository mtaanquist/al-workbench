using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace ALDevToolbox.Services.Mcp;

/// <summary>
/// Holds a ZIP that <c>generate_workspace</c> or <c>generate_extension</c> made until the
/// person who asked for it fetches it from <c>/generate/download/{token}</c> (#1508).
///
/// <para>The bytes used to come back inline as base64, and a typical workspace was already
/// past what an assistant client accepts in one answer. Generation is not repeatable (every
/// run mints new extension GUIDs), so the link cannot regenerate on fetch; the archive is
/// kept instead. It lives in the process's memory cache rather than a table: a link only
/// has to outlast the minute between the tool answering and the user clicking it, and
/// generation stays read-only against the database. A restart loses the open links, which
/// the tool's description says.</para>
///
/// <para>The token is 256 random bits, and it is not enough on its own: a fetch must come
/// from the same user in the same organisation that generated it, so a link pasted into a
/// chat is no use to anyone else.</para>
/// </summary>
public sealed class GeneratedDownloadStore
{
    /// <summary>How long a link works after the tool answers.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>The route the endpoint serves, with the token appended.</summary>
    public const string PathPrefix = "/generate/download/";

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public GeneratedDownloadStore(IMemoryCache cache, TimeProvider clock)
    {
        _cache = cache;
        _clock = clock;
    }

    /// <summary>Keeps <paramref name="content"/> for <paramref name="userId"/> and returns the link to it.</summary>
    public GeneratedDownloadLink Put(int organizationId, int userId, string fileName, byte[] content)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = _clock.GetUtcNow() + Lifetime;
        _cache.Set(Key(token), new Entry(organizationId, userId, fileName, content, expiresAt),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });
        return new GeneratedDownloadLink(PathPrefix + token, expiresAt.UtcDateTime);
    }

    /// <summary>
    /// The archive <paramref name="token"/> names, when it has not expired and was made by
    /// this user in this organisation; null otherwise, the same answer for every reason so
    /// a fetch cannot tell someone else's link from one that never existed.
    /// </summary>
    public GeneratedDownload? Get(string token, int? organizationId, int? userId)
    {
        if (organizationId is null || userId is null) return null;
        if (!_cache.TryGetValue(Key(token), out Entry? entry) || entry is null) return null;
        if (entry.ExpiresAt <= _clock.GetUtcNow()) return null;
        if (entry.OrganizationId != organizationId || entry.UserId != userId) return null;
        return new GeneratedDownload(entry.FileName, entry.Content);
    }

    private static string Key(string token) => "mcp-generated-download:" + token;

    private sealed record Entry(int OrganizationId, int UserId, string FileName, byte[] Content, DateTimeOffset ExpiresAt);
}

/// <summary>Where a generated ZIP can be fetched, and until when.</summary>
public sealed record GeneratedDownloadLink(string DownloadPath, DateTime ExpiresAt);

/// <summary>A generated ZIP, as the download endpoint serves it.</summary>
public sealed record GeneratedDownload(string FileName, byte[] Content);
