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
/// kept instead. It lives in process memory rather than a table: a link only has to
/// outlast the half hour between the tool answering and the user clicking it, and
/// generation stays read-only against the database. A restart loses the open links, which
/// the tool's description says.</para>
///
/// <para>The store has its own cache with a size limit of <see cref="MaxHeldBytes"/>,
/// counted in archive bytes, so an assistant retrying a generate call in a loop cannot
/// pile up gigabytes. The shared <see cref="IMemoryCache"/> has no limit, and giving it one
/// would make every other entry in it need a size.</para>
///
/// <para>The token is 256 random bits, and it is not enough on its own: a fetch must come
/// from the same user in the same organisation that generated it, so a link pasted into a
/// chat is no use to anyone else.</para>
/// </summary>
public sealed class GeneratedDownloadStore : IDisposable
{
    /// <summary>How long a link works after the tool answers.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>The route the endpoint serves, with the token appended.</summary>
    public const string PathPrefix = "/generate/download/";

    /// <summary>How many archive bytes the store holds at once, across everyone.</summary>
    public const long MaxHeldBytes = 256L * 1024 * 1024;

    private readonly MemoryCache _cache;
    private readonly TimeProvider _clock;

    public GeneratedDownloadStore(TimeProvider clock) : this(clock, MaxHeldBytes) { }

    internal GeneratedDownloadStore(TimeProvider clock, long maxHeldBytes)
    {
        _clock = clock;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxHeldBytes });
    }

    /// <summary>
    /// Keeps <paramref name="content"/> for <paramref name="userId"/> and returns the link
    /// to it, or null when the store is full. A full store sheds its oldest links in the
    /// background, so a later call can succeed.
    /// </summary>
    public GeneratedDownloadLink? Put(int organizationId, int userId, string fileName, byte[] content)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = _clock.GetUtcNow() + Lifetime;
        _cache.Set(Key(token), new Entry(organizationId, userId, fileName, content, expiresAt),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Lifetime,
                // At least 1, as the cache refuses a zero-size entry under a limit.
                Size = Math.Max(1, content.LongLength),
            });
        // Over the limit the cache drops the entry rather than throwing.
        return _cache.TryGetValue(Key(token), out _)
            ? new GeneratedDownloadLink(PathPrefix + token, expiresAt.UtcDateTime)
            : null;
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

    public void Dispose() => _cache.Dispose();

    private static string Key(string token) => "mcp-generated-download:" + token;

    private sealed record Entry(int OrganizationId, int UserId, string FileName, byte[] Content, DateTimeOffset ExpiresAt);
}

/// <summary>Where a generated ZIP can be fetched, and until when.</summary>
public sealed record GeneratedDownloadLink(string DownloadPath, DateTime ExpiresAt);

/// <summary>A generated ZIP, as the download endpoint serves it.</summary>
public sealed record GeneratedDownload(string FileName, byte[] Content);
