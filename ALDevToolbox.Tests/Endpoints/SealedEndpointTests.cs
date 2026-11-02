using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Sealed;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The recipient's half of Sealed over the real stack (.design/sealed.md): the page
/// and the reveal answer without an account, loading the page spends nothing, the
/// reveal is never cached, and the sender's page still needs a sign-in.
///
/// <para>The cases share one host (#1536, #1582). Each creates its own links from
/// a sender of its own. The one that switches the tool off site-wide boots a host
/// of its own.</para>
/// </summary>
public sealed class SealedEndpointTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;

    public SealedEndpointTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
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
    public async Task The_recipient_page_is_never_cached_and_asks_for_a_passphrase_when_the_link_has_one()
    {
        var (token, _) = await CreateLinkAsync(withPassphrase: true);
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/s/{token}");
        var html = await response.Content.ReadAsStringAsync();

        response.Headers.CacheControl!.NoStore.Should().BeTrue("a revealed secret must not come back from the page cache");
        html.Should().Contain("data-salt=\"").And.Contain("data-sealed-passphrase");
    }

    [Fact]
    public async Task Switching_the_tool_off_site_wide_closes_every_link()
    {
        var (token, check) = await CreateLinkAsync();
        var off = new ToolAvailabilityState(new McpOff());
        off.Set([ToolKey.Sealed]);
        using var factory = new EndpointFactory(_db, services => services.AddSingleton<IToolAvailability>(off));
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync($"/s/{token}");
        using var reveal = await client.PostAsJsonAsync($"/api/sealed/{token}/reveal", new { check });

        html.Should().Contain("This link no longer works").And.NotContain("Show secret");
        reveal.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_senders_page_needs_a_sign_in()
    {
        using var client = _factory.CreateClient(handleCookies: false);

        using var response = await client.GetAsync("/sealed");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Contain("login");
    }

    [Fact]
    public async Task The_signed_in_creator_can_open_their_own_link_without_spending_the_view()
    {
        var (token, check, email) = await CreateLinkWithSenderAsync();
        using var creator = await SignInAsync(email);

        var html = await creator.GetStringAsync($"/s/{token}");
        using var mine = await creator.PostAsJsonAsync($"/api/sealed/{token}/reveal", new { check });

        html.Should().Contain("This is a link you created").And.Contain("data-creator=\"true\"");
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("creator").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("viewsLeft").GetInt32().Should().Be(1);
        }

        using var recipient = _factory.CreateClient(handleCookies: false);
        (await recipient.GetStringAsync($"/s/{token}")).Should().Contain("sent you a secret").And.NotContain("data-creator");
        using var theirs = await recipient.PostAsJsonAsync($"/api/sealed/{token}/reveal", new { check });
        theirs.StatusCode.Should().Be(HttpStatusCode.OK, "the creator's look left the one view for the recipient");
        using (var doc = JsonDocument.Parse(await theirs.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("creator").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("viewsLeft").GetInt32().Should().Be(0);
        }
    }

    private async Task<(string Token, string Check, string Email)> CreateLinkWithSenderAsync()
    {
        var email = $"alex{Guid.NewGuid():N}@cronus.example";
        await using (var ctx = _db.NewContext())
        {
            ctx.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = "Alex Hansen",
                PasswordHash = _host.HashPassword(Password),
                Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var (token, check) = await CreateLinkAsync(senderEmail: email);
        return (token, check, email);
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();
        using var page = await client.GetAsync("/login");
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the sign-in page must carry an antiforgery token");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = Password, ["__RequestVerificationToken"] = match.Groups[1].Value,
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private sealed class McpOff : IMcpAvailability
    {
        public bool IsEnabled => false;
    }

    private async Task<(string Token, string Check)> CreateLinkAsync(bool withPassphrase = false, string? senderEmail = null)
    {
        await using var ctx = _db.NewContext();
        var user = senderEmail is null ? null : await ctx.Users.SingleAsync(u => u.Email == senderEmail);
        if (user is null)
        {
            user = new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = $"alex{Guid.NewGuid():N}@cronus.example", DisplayName = "Alex Hansen",
                PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = user.Id;

        var check = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var token = await new SealedLinkService(ctx, _db.OrgContext, TimeProvider.System, NullLogger<SealedLinkService>.Instance)
            .CreateAsync(new SealedLinkInput(
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48)),
                Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)),
                check,
                withPassphrase ? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)) : null,
                withPassphrase ? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)) : null,
                24, 1, null));
        return (token, check);
    }
}
