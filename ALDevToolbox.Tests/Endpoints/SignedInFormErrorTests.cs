using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The signed-in Account page and authenticator setup take a reason code,
/// never a sentence, from the address, as the signed-out forms do: a link
/// someone sends a signed-in user must not make the real page show their
/// text as an error (#1544, AUTH-2). The copy lives in
/// <see cref="FormErrorReasons"/>.
///
/// <para>The cases share one host. Every test signs in an address no other
/// test uses.</para>
/// </summary>
public sealed class SignedInFormErrorTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";
    private const string Planted = "Call CRONUS support on +45 0000 0000";

    private readonly SharedEndpointHost _host;

    public SignedInFormErrorTests(SharedEndpointHost host) => _host = host;

    [Fact]
    public async Task A_wrong_password_comes_back_as_a_code()
    {
        var client = await SignInAsync("wrong-pw-code@cronus.example");
        using var page = await client.GetAsync("/account/security");

        using var response = await PostAsync(client, "/auth/account/2fa/email/disable",
            AntiforgeryToken(await page.Content.ReadAsStringAsync()), ("Password", "not it"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().Be($"/account/security?err=Password&reason={FormErrorReasons.PasswordWrong}");
        location.Should().NotContain("msg=");
    }

    [Fact]
    public async Task A_refused_password_change_comes_back_as_a_code()
    {
        var client = await SignInAsync("change-pw-code@cronus.example");
        using var page = await client.GetAsync("/account/security");

        using var response = await PostAsync(client, "/auth/account/password",
            AntiforgeryToken(await page.Content.ReadAsStringAsync()),
            ("CurrentPassword", Password), ("NewPassword", "short"));

        response.Headers.Location!.OriginalString
            .Should().Be("/account/security?err=NewPassword&reason=password-short");
    }

    [Fact]
    public async Task The_account_page_shows_its_own_sentence_for_a_code_and_never_the_addresss_text()
    {
        var client = await SignInAsync("account-page-code@cronus.example");

        var known = await BodyAsync(client, $"/account/security?err=Password&reason={FormErrorReasons.PasswordWrong}");
        known.Should().Contain("Password is incorrect.");

        var planted = await BodyAsync(client,
            "/account/security?err=Password&msg=" + Uri.EscapeDataString(Planted) + "&reason=" + Uri.EscapeDataString(Planted));
        planted.Should().NotContain(Planted);
        planted.Should().Contain(FormErrorReasons.GenericMessage);
    }

    [Fact]
    public async Task Authenticator_setup_never_shows_the_addresss_text()
    {
        var client = await SignInAsync("totp-page-code@cronus.example");

        var body = await BodyAsync(client,
            "/account/2fa/totp/setup?err=" + Uri.EscapeDataString(Planted) + "&msg=" + Uri.EscapeDataString(Planted));

        body.Should().NotContain(Planted);
    }

    [Fact]
    public void The_sentences_the_signed_in_services_throw_round_trip()
    {
        foreach (var message in new[]
                 {
                     AuthService.CurrentPasswordLockedOutMessage,
                     EntraSignInService.IdentityTakenMessage,
                     "Current password is incorrect.",
                     "That code didn't match. Check your authenticator and try again.",
                     "Give the token a recognisable name.",
                     "Name must be 1-80 characters.",
                 })
        {
            var code = FormErrorReasons.CodeFor(message);
            code.Should().NotBe(FormErrorReasons.Generic, $"\"{message}\" is one the services throw");
            FormErrorReasons.MessageFor(code).Should().Be(message);
        }
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        await using (var seed = _host.Db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                DisplayName = "Kirsten Jensen",
                PasswordHash = _host.HashPassword(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var client = _host.Factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await PostAsync(client, "/auth/login",
            AntiforgeryToken(await loginPage.Content.ReadAsStringAsync()), ("Email", email), ("Password", Password));
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> BodyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
