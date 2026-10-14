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
/// How long a confirmation counts for adding a passkey: the organisation's
/// step-up window, but never more than <see cref="StepUpService.PasskeyWindowCap"/>,
/// because a passkey outlives the session that adds it. Its own class because
/// it boots the app on a clock the test moves.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class PasskeyRegistrationWindowTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly EndpointFactory _factory;

    public PasskeyRegistrationWindowTests()
    {
        _factory = new EndpointFactory(_db,
            services => services.AddSingleton<TimeProvider>(_clock),
            new Dictionary<string, string?>
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
    public async Task An_hour_long_tool_window_still_gives_a_passkey_only_fifteen_minutes()
    {
        await using (var seed = _db.NewContext())
        {
            var org = await seed.Organizations.FirstAsync(o => o.Id == TestDb.DefaultOrgId);
            org.StepUpWindowMinutes = 60;
            await seed.SaveChangesAsync();
        }
        var recoveryCodes = await SeedTotpUserAsync("window@cronus.example");
        using var client = await SignInWithRecoveryCodeAsync("window@cronus.example", recoveryCodes[0]);
        var token = await PageTokenAsync(client);

        _clock.Advance(TimeSpan.FromMinutes(10));
        using var inside = await StartAsync(client, token);
        inside.StatusCode.Should().Be(HttpStatusCode.OK, "ten minutes is inside both windows");

        _clock.Advance(TimeSpan.FromMinutes(10));
        using var outside = await StartAsync(client, token);
        outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, "twenty minutes is inside the hour but past the cap");
        (await outside.Content.ReadAsStringAsync()).Should().Contain("confirmation ran out");
    }

    private async Task<IReadOnlyList<string>> SeedTotpUserAsync(string email)
    {
        int userId;
        await using (var seed = _db.NewContext())
        {
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
            userId = user.Id;
        }
        await using var scope = _factory.Services.CreateAsyncScope();
        var totp = scope.ServiceProvider.GetRequiredService<TotpService>();
        var enrollment = await totp.BeginEnrollmentAsync(userId);
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(enrollment.SecretBase32)).ComputeTotp(_clock.GetUtcNow().UtcDateTime);
        return await totp.ConfirmEnrollmentAsync(userId, code);
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

    private static async Task<string> PageTokenAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/account/security");
        return FormToken(await page.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AccountPasskeyEndpoints.RegistrationOptionsPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string FormToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
