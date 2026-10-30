using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Tests.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The link a generate_* tool hands back (#1508): it serves the archive to the person who
/// generated it, in their organisation, until it expires, and to nobody else.
/// </summary>
public sealed class GeneratedDownloadStoreTests
{
    private const int OrgId = 7;
    private const int UserId = 42;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly GeneratedDownloadStore _store;

    public GeneratedDownloadStoreTests()
    {
        _store = new GeneratedDownloadStore(new MemoryCache(new MemoryCacheOptions()), _clock);
    }

    private static string TokenOf(GeneratedDownloadLink link) =>
        link.DownloadPath[GeneratedDownloadStore.PathPrefix.Length..];

    [Fact]
    public void The_link_serves_the_archive_to_the_user_who_generated_it_until_it_expires()
    {
        var link = _store.Put(OrgId, UserId, "CRONUS.zip", [1, 2, 3]);

        link.DownloadPath.Should().StartWith(GeneratedDownloadStore.PathPrefix);
        link.ExpiresAt.Should().Be(_clock.GetUtcNow().UtcDateTime + GeneratedDownloadStore.Lifetime);
        var file = _store.Get(TokenOf(link), OrgId, UserId);
        file!.FileName.Should().Be("CRONUS.zip");
        file.Content.Should().Equal(1, 2, 3);
        _store.Get(TokenOf(link), OrgId, UserId).Should().NotBeNull("a second fetch works too, for a retried download");

        _clock.Advance(GeneratedDownloadStore.Lifetime);
        _store.Get(TokenOf(link), OrgId, UserId).Should().BeNull("the link has expired");
    }

    [Fact]
    public void Nobody_else_can_use_the_link()
    {
        var token = TokenOf(_store.Put(OrgId, UserId, "CRONUS.zip", [1]));

        _store.Get(token, OrgId, UserId + 1).Should().BeNull("another user in the same organisation");
        _store.Get(token, OrgId + 1, UserId).Should().BeNull("the same user id in another organisation");
        _store.Get(token, null, null).Should().BeNull("a request with nobody signed in");
        _store.Get("not-a-token", OrgId, UserId).Should().BeNull();
    }

    [Fact]
    public void Each_link_gets_its_own_unguessable_token()
    {
        var first = TokenOf(_store.Put(OrgId, UserId, "a.zip", [1]));
        var second = TokenOf(_store.Put(OrgId, UserId, "a.zip", [1]));

        first.Should().NotBe(second);
        first.Length.Should().BeGreaterThanOrEqualTo(43, "256 random bits, base64url without padding");
        first.Should().MatchRegex("^[A-Za-z0-9_-]+$", "the token sits in a URL path");
    }
}
