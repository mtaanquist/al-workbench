using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Sealed;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The recipient's half of Sealed over the real stack (.design/sealed.md): the page
/// and the reveal answer without an account, loading the page spends nothing, the
/// reveal is never cached, and the sender's page still needs a sign-in.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class SealedEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public SealedEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_recipient_page_shows_the_sender_and_spends_nothing()
    {
        var (token, _) = await CreateLinkAsync();
        using var client = _factory.CreateClient();

        var first = await client.GetStringAsync($"/s/{token}");
        var second = await client.GetStringAsync($"/s/{token}");

        first.Should().Contain("sent you a secret").And.Contain("Show secret").And.Contain("noindex");
        second.Should().Contain("sent you a secret", "loading the page twice must not use the link up");
    }

    [Fact]
    public async Task An_unknown_link_says_it_no_longer_works()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/s/AAAAAAAAAAAAAAAAAAAAAA");

        html.Should().Contain("This link no longer works").And.NotContain("Show secret");
    }

    [Fact]
    public async Task The_reveal_serves_anonymously_once_and_is_never_cached()
    {
        var (token, check) = await CreateLinkAsync();
        using var client = _factory.CreateClient();

        using var revealed = await client.PostAsJsonAsync($"/api/sealed/{token}/reveal", new { check });
        revealed.StatusCode.Should().Be(HttpStatusCode.OK);
        revealed.Headers.CacheControl!.NoStore.Should().BeTrue();
        using (var doc = JsonDocument.Parse(await revealed.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("ciphertext").GetString().Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("viewsLeft").GetInt32().Should().Be(0);
        }

        using var again = await client.PostAsJsonAsync($"/api/sealed/{token}/reveal", new { check });
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_senders_page_needs_a_sign_in()
    {
        using var client = _factory.CreateClient(handleCookies: false);

        using var response = await client.GetAsync("/sealed");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("login");
    }

    private async Task<(string Token, string Check)> CreateLinkAsync()
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = $"alex{Guid.NewGuid():N}@cronus.example", DisplayName = "Alex Hansen",
            PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = user.Id;

        var check = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = await new SealedLinkService(ctx, _db.OrgContext, TimeProvider.System, NullLogger<SealedLinkService>.Instance)
            .CreateAsync(new SealedLinkInput(
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48)),
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)),
                check, null, null, 24, 1, null));
        return (token, check);
    }
}
