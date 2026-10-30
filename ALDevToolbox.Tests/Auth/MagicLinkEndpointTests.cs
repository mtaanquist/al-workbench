using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The emailed sign-in link through the real pipeline (#1500). Mail scanners
/// and chat link previews fetch a link before the person clicks it, so no GET
/// may spend the token or sign anyone in; only the confirmation page's POST does.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class MagicLinkEndpointTests : IDisposable
{
    private const string AuthCookie = "alwb_auth";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public MagicLinkEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Opening_the_link_signs_nobody_in_and_leaves_it_usable()
    {
        var token = await IssueTokenAsync("scanned@cronus.example");
        var scanner = _factory.CreateClient();

        using var page = await scanner.GetAsync($"/login/magic/confirm?token={Uri.EscapeDataString(token)}");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        SetsAuthCookie(page).Should().BeFalse("opening the link must not sign the visitor in");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("action=\"/auth/login/magic/consume\"");
        html.Should().Contain("noindex");

        // The person's own click afterwards still works.
        using var signIn = await SignInAsync(_factory.CreateClient(), token);
        signIn.Headers.Location!.OriginalString.Should().Be("/");
        SetsAuthCookie(signIn).Should().BeTrue();
    }

    [Fact]
    public async Task An_old_style_link_forwards_to_the_confirmation_page_without_spending_it()
    {
        var token = await IssueTokenAsync("old-link@cronus.example");
        var scanner = _factory.CreateClient();

        using var old = await scanner.GetAsync($"/auth/login/magic/consume?token={Uri.EscapeDataString(token)}");

        old.StatusCode.Should().Be(HttpStatusCode.Redirect);
        old.Headers.Location!.OriginalString.Should().Be($"/login/magic/confirm?token={Uri.EscapeDataString(token)}");
        SetsAuthCookie(old).Should().BeFalse();

        using var signIn = await SignInAsync(_factory.CreateClient(), token);
        signIn.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task The_sign_in_button_spends_the_link_once()
    {
        var token = await IssueTokenAsync("twice@cronus.example");

        using var first = await SignInAsync(_factory.CreateClient(), token);
        first.Headers.Location!.OriginalString.Should().Be("/");

        using var second = await SignInAsync(_factory.CreateClient(), token);
        second.Headers.Location!.OriginalString.Should().StartWith("/login/magic?err=invalid");
        SetsAuthCookie(second).Should().BeFalse();
    }

    [Fact]
    public async Task A_post_without_the_page_form_is_refused_and_leaves_the_link_usable()
    {
        var token = await IssueTokenAsync("forged@cronus.example");

        using var forged = await _factory.CreateClient().PostAsync("/auth/login/magic/consume",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("Token", token)]));

        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SetsAuthCookie(forged).Should().BeFalse();

        using var signIn = await SignInAsync(_factory.CreateClient(), token);
        signIn.Headers.Location!.OriginalString.Should().Be("/");
    }

    /// <summary>Opens the confirmation page and presses Sign in, as the person does.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string token)
    {
        using var page = await client.GetAsync($"/login/magic/confirm?token={Uri.EscapeDataString(token)}");
        var html = await page.Content.ReadAsStringAsync();
        var antiforgery = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        antiforgery.Should().NotBeEmpty();

        var response = await client.PostAsync("/auth/login/magic/consume", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("Token", token),
            new KeyValuePair<string, string>("__RequestVerificationToken", antiforgery),
        ]));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return response;
    }

    private async Task<string> IssueTokenAsync(string email)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                DisplayName = "Nils Consultant",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<PasswordResetService>()
            .CreateMagicLoginTokenAsync(email, "203.0.113.7");
        token.Should().NotBeNull();
        return token!;
    }

    private static bool SetsAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(AuthCookie + "=", StringComparison.Ordinal));
}
