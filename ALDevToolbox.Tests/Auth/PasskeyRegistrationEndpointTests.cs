using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Adding a passkey through the real pipeline: it needs the antiforgery token
/// on both legs and a recent second step (or, from an account with nothing to
/// step up with, the current password). And what a password reset does and
/// does not do to passkeys: they stay, and the person is asked to look them
/// over. See ".design/auth-and-audit.md", "Passkeys (WebAuthn)".
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class PasskeyRegistrationEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "another horse battery staple";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public PasskeyRegistrationEndpointTests()
    {
        _factory = new EndpointFactory(_db, environment: new Dictionary<string, string?>
        {
            ["Auth__WebAuthn__RpId"] = "localhost",
            ["Auth__WebAuthn__OriginsCsv"] = "https://localhost",
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Starting_without_the_antiforgery_token_is_refused()
    {
        await SeedUserAsync("no-token@cronus.example");
        using var client = await SignInWithPasswordAsync("no-token@cronus.example");

        using var response = await client.PostAsync(AccountPasskeyEndpoints.RegistrationOptionsPath,
            Json($$"""{"password":"{{Password}}"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith(PasskeyService.RegistrationCookieName));
    }

    [Fact]
    public async Task Finishing_without_the_antiforgery_token_is_refused()
    {
        var userId = await SeedUserAsync("no-token-finish@cronus.example");
        using var client = await SignInWithPasswordAsync("no-token-finish@cronus.example");

        using var response = await client.PostAsync("/account/passkeys",
            Json("""{"nickname":"Work laptop","response":{"id":"AA","rawId":"AA","type":"public-key","response":{}}}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Antiforgery");
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().AnyAsync(p => p.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task An_account_with_nothing_to_step_up_with_confirms_with_its_current_password()
    {
        await SeedUserAsync("password-only@cronus.example");
        using var client = await SignInWithPasswordAsync("password-only@cronus.example");
        var token = await PageTokenAsync(client);

        using var wrong = await StartAsync(client, token, "not my password");
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("Password is incorrect.");

        using var right = await StartAsync(client, token, Password);
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        (await right.Content.ReadAsStringAsync()).Should().Contain("challenge");
        right.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith(PasskeyService.RegistrationCookieName));
    }

    [Fact]
    public async Task An_account_that_can_step_up_has_to_before_adding_a_passkey()
    {
        // It already has a passkey, so it can confirm with that; a session
        // that only typed the password has not, and the password is no
        // substitute.
        await SeedUserAsync("has-passkey@cronus.example", withPasskey: true);
        using var client = await SignInWithPasswordAsync("has-passkey@cronus.example");
        var token = await PageTokenAsync(client);

        using var response = await StartAsync(client, token, Password);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(
            System.Text.Json.JsonSerializer.Serialize(AccountPasskeyEndpoints.StepUpFirstMessage)[1..^1]);
    }

    [Fact]
    public async Task A_session_that_just_confirmed_a_second_step_adds_one_without_a_password()
    {
        var recoveryCodes = await SeedTotpUserAsync("fresh@cronus.example");
        using var client = await SignInWithRecoveryCodeAsync("fresh@cronus.example", recoveryCodes[0]);
        var token = await PageTokenAsync(client);

        using var response = await StartAsync(client, token, password: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("challenge");
    }

    [Fact]
    public async Task A_password_reset_keeps_passkeys_and_asks_the_person_to_check_them()
    {
        var userId = await SeedUserAsync("reset@cronus.example", withPasskey: true);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var reset = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            var raw = await reset.CreatePasswordResetTokenAsync("reset@cronus.example", "203.0.113.7");
            await reset.ConsumePasswordResetTokenAsync(raw!, NewPassword);
        }

        using var anonymous = _factory.CreateClient();
        using var login = await anonymous.GetAsync("/login?ok=password-reset");
        (await login.Content.ReadAsStringAsync()).Should().Contain("remove any you don");

        using var client = await SignInWithPasswordAsync("reset@cronus.example", NewPassword);
        using var page = await client.GetAsync("/account/security");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("Software key").And.Contain("check you recognise every one");

        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().CountAsync(p => p.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Without_a_recent_password_change_the_passkey_card_asks_nothing()
    {
        await SeedUserAsync("calm@cronus.example", withPasskey: true);
        using var client = await SignInWithPasswordAsync("calm@cronus.example");

        using var page = await client.GetAsync("/account/security");

        (await page.Content.ReadAsStringAsync()).Should().NotContain("check you recognise every one");
    }

    private async Task<int> SeedUserAsync(string email, bool withPasskey = false)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Nils Consultant",
            PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        if (withPasskey)
        {
            seed.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                CredentialId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
                PublicKey = [0xA0],
                Nickname = "Software key",
                CreatedAt = DateTime.UtcNow.AddDays(-30),
            });
            await seed.SaveChangesAsync();
        }
        return user.Id;
    }

    private async Task<IReadOnlyList<string>> SeedTotpUserAsync(string email)
    {
        var userId = await SeedUserAsync(email);
        await using var scope = _factory.Services.CreateAsyncScope();
        var totp = scope.ServiceProvider.GetRequiredService<TotpService>();
        var enrollment = await totp.BeginEnrollmentAsync(userId);
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(enrollment.SecretBase32)).ComputeTotp(DateTime.UtcNow);
        return await totp.ConfirmEnrollmentAsync(userId, code);
    }

    private async Task<HttpClient> SignInWithPasswordAsync(string email, string password = Password)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostFormAsync(client, "/auth/login", token, ("Email", email), ("Password", password));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private async Task<HttpClient> SignInWithRecoveryCodeAsync(string email, string recoveryCode)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostFormAsync(client, "/auth/login", token, ("Email", email), ("Password", Password));
        login.Headers.Location!.OriginalString.Should().StartWith("/login/challenge");
        using var done = await PostFormAsync(client, "/auth/login/challenge/recovery", token, ("Code", recoveryCode));
        done.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    /// <summary>The token the passkey card renders, as the page script reads it.</summary>
    private static async Task<string> PageTokenAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/account/security");
        return FormToken(await page.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string token, string? password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AccountPasskeyEndpoints.RegistrationOptionsPath)
        {
            Content = Json(System.Text.Json.JsonSerializer.Serialize(new { password })),
        };
        request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string FormToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
