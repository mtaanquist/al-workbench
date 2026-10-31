using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The two-step endpoints through the real pipeline: the challenge
/// throttle as the endpoints wire it (#1271), and confirming a two-step
/// change on an account that signs in with Microsoft and has no password
/// it knows (#1259).
///
/// <para>The cases share one host (#1536). Every test signs in an address no
/// other test uses, so the attempts one test counts are only its own.</para>
/// </summary>
public sealed class MfaEndpointTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;

    public MfaEndpointTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
    }

    [Fact]
    public async Task The_totp_challenge_refuses_after_five_wrong_codes_even_a_right_one()
    {
        var (_, secret, _) = await SeedTotpUserAsync("throttle-totp@cronus.example");
        var (client, token) = await PasswordLegAsync("throttle-totp@cronus.example");

        for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
        {
            using var wrong = await PostAsync(client, "/auth/login/challenge/totp", token, ("Code", "000000"));
            wrong.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }

        using var right = await PostAsync(client, "/auth/login/challenge/totp", token, ("Code", CurrentCode(secret)));
        right.Headers.Location!.OriginalString.Should().Contain("err=too-many-attempts");
    }

    [Fact]
    public async Task The_recovery_challenge_refuses_after_five_wrong_codes_even_a_right_one()
    {
        var (_, _, recoveryCodes) = await SeedTotpUserAsync("throttle-recovery@cronus.example");
        var (client, token) = await PasswordLegAsync("throttle-recovery@cronus.example");

        for (var i = 0; i < AuthService.MaxMfaAttempts; i++)
        {
            using var wrong = await PostAsync(client, "/auth/login/challenge/recovery", token, ("Code", "AAAAA-BBBBB"));
            wrong.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }

        using var right = await PostAsync(client, "/auth/login/challenge/recovery", token, ("Code", recoveryCodes[0]));
        right.Headers.Location!.OriginalString.Should().Contain("err=too-many-attempts");
    }

    [Fact]
    public async Task A_typo_shaped_totp_code_does_not_count_toward_the_lockout()
    {
        const string email = "typo-totp@cronus.example";
        await SeedTotpUserAsync(email);
        var (client, token) = await PasswordLegAsync(email);

        // The shared counter covers every second-factor method, so typos here would
        // lock the person out of recovery codes too.
        foreach (var typo in new[] { "", "12345", "abcdef", "1234567", "12345" })
        {
            using var guess = await PostAsync(client, "/auth/login/challenge/totp", token, ("Code", typo));
            guess.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }

        (await CountFailedAttemptsAsync(email)).Should().Be(0, "a code that cannot be right is not a guess");
    }

    [Fact]
    public async Task A_typo_shaped_recovery_code_does_not_count_toward_the_lockout()
    {
        const string email = "typo-recovery@cronus.example";
        var (_, _, recoveryCodes) = await SeedTotpUserAsync(email);
        var (client, token) = await PasswordLegAsync(email);

        foreach (var typo in new[] { "", "AAAA-BBBB", "AAAAA-BBBBBB", "-", "AAAAA" })
        {
            using var guess = await PostAsync(client, "/auth/login/challenge/recovery", token, ("Code", typo));
            guess.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }
        (await CountFailedAttemptsAsync(email)).Should().Be(0, "a code that cannot be right is not a guess");

        using var right = await PostAsync(client, "/auth/login/challenge/recovery", token, ("Code", recoveryCodes[0]));
        right.Headers.Location!.OriginalString.Should().NotContain("err=");
    }

    [Fact]
    public async Task Sending_new_email_codes_does_not_reset_the_shared_cap()
    {
        // Each new email code gets fresh guesses of its own (#1432); the shared
        // second-factor counter is what keeps re-sends from adding up.
        var userId = await SeedEmailMfaUserAsync("throttle-email@cronus.example");
        var (client, token) = await PasswordLegAsync("throttle-email@cronus.example");

        // Five wrong guesses spread over three codes (2 + 2 + 1), so no single
        // code reaches its own cap and only the shared counter can refuse.
        string code = "";
        foreach (var guesses in new[] { 2, 2, 1 })
        {
            code = await IssueEmailCodeAsync(userId);
            var wrong = code == "000000" ? "111111" : "000000";
            for (var i = 0; i < guesses; i++)
            {
                using var guess = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", wrong));
                guess.Headers.Location!.OriginalString.Should().Contain("err=invalid");
            }
        }

        using var right = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", code));
        right.Headers.Location!.OriginalString.Should().Contain("err=too-many-attempts");
    }

    [Fact]
    public async Task A_right_email_code_gives_its_attempt_back()
    {
        const string email = "release-email@cronus.example";
        var userId = await SeedEmailMfaUserAsync(email);
        var (client, token) = await PasswordLegAsync(email);
        var code = await IssueEmailCodeAsync(userId);
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < AuthService.MaxMfaAttempts - 1; i++)
        {
            using var guess = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", wrong));
            guess.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }
        var failuresBefore = await CountFailedAttemptsAsync(email);

        using var right = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", code));

        right.Headers.Location!.OriginalString.Should().NotContain("err=");
        (await CountFailedAttemptsAsync(email)).Should().Be(failuresBefore, "a right code is not a failed attempt");
    }

    [Fact]
    public async Task A_typo_shaped_email_code_does_not_count_toward_the_lockout()
    {
        const string email = "typo-email@cronus.example";
        var userId = await SeedEmailMfaUserAsync(email);
        var (client, token) = await PasswordLegAsync(email);
        var code = await IssueEmailCodeAsync(userId);

        // An empty box, a short code and letters are typos, not guesses at the code.
        foreach (var typo in new[] { "", "12345", "abcdef", "1234567" })
        {
            using var guess = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", typo));
            guess.Headers.Location!.OriginalString.Should().Contain("err=invalid");
        }
        (await CountFailedAttemptsAsync(email)).Should().Be(0, "a code that cannot be right is not a guess");

        using var right = await PostAsync(client, "/auth/login/challenge/email/verify", token, ("Code", code));
        right.Headers.Location!.OriginalString.Should().NotContain("err=");
    }

    private async Task<int> CountFailedAttemptsAsync(string email)
    {
        await using var read = _db.NewContext();
        return await read.LoginAttempts.IgnoreQueryFilters().CountAsync(a => a.Email == email && !a.Succeeded);
    }

    [Fact]
    public async Task A_microsoft_linked_account_with_a_fresh_second_step_turns_off_totp_without_a_password()
    {
        var (userId, _, recoveryCodes) = await SeedTotpUserAsync("entra-only@cronus.example", linkMicrosoft: true);
        using var client = await SignInWithRecoveryCodeAsync("entra-only@cronus.example", recoveryCodes[0]);

        using var page = await client.GetAsync("/account/security");
        using var response = await PostAsync(client, "/auth/account/2fa/totp/disable",
            AntiforgeryToken(await page.Content.ReadAsStringAsync()), ("Password", ""));

        response.Headers.Location!.OriginalString.Should().Contain("ok=totp-disabled");
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).TotpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Coming_back_from_the_confirm_step_says_it_worked()
    {
        // "Confirm with Microsoft" goes through the step-up and back to the
        // security tab; the user should not land there with nothing to say
        // the confirmation took.
        var (_, _, recoveryCodes) = await SeedTotpUserAsync("confirmed@cronus.example", linkMicrosoft: true);
        using var client = await SignInWithRecoveryCodeAsync("confirmed@cronus.example", recoveryCodes[0]);

        // Already fresh from the recovery code, so the step-up hands straight back.
        using var stepUp = await client.GetAsync("/auth/step-up?return=" + Uri.EscapeDataString("/account/security?ok=confirmed"));
        stepUp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var back = stepUp.Headers.Location!.OriginalString;
        back.Should().Be("/account/security?ok=confirmed");

        using var page = await client.GetAsync(back);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("confirmed for the next few minutes. You can now get new recovery codes");
    }

    [Fact]
    public async Task An_account_without_a_microsoft_link_still_needs_its_password_to_turn_off_totp()
    {
        var (userId, _, recoveryCodes) = await SeedTotpUserAsync("password-only@cronus.example");
        using var client = await SignInWithRecoveryCodeAsync("password-only@cronus.example", recoveryCodes[0]);

        using var page = await client.GetAsync("/account/security");
        using var response = await PostAsync(client, "/auth/account/2fa/totp/disable",
            AntiforgeryToken(await page.Content.ReadAsStringAsync()), ("Password", ""));

        response.Headers.Location!.OriginalString.Should().Contain("err=Password");
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).TotpEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_microsoft_linked_account_whose_confirmation_lapsed_is_told_to_confirm_again_not_that_its_password_is_wrong()
    {
        // The page hides the password box while the session is fresh; when the
        // window closes before the form is sent, the post arrives with no
        // password. Here the session was never fresh (password only, no
        // second factor), which is the same post the server sees.
        await SeedPasswordOnlyMicrosoftUserAsync("lapsed@cronus.example");
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await PostAsync(client, "/auth/login",
            AntiforgeryToken(await loginPage.Content.ReadAsStringAsync()),
            ("Email", "lapsed@cronus.example"), ("Password", Password));
        login.Headers.Location!.OriginalString.Should().NotContain("err=");

        using var page = await client.GetAsync("/account/security");
        using var response = await PostAsync(client, "/auth/account/2fa/email/disable",
            AntiforgeryToken(await page.Content.ReadAsStringAsync()), ("Password", ""));

        var location = Uri.UnescapeDataString(response.Headers.Location!.OriginalString);
        location.Should().Contain("Your confirmation has expired");
        location.Should().NotContain("Password is incorrect");
    }

    [Fact]
    public async Task Turning_the_authenticator_app_on_and_off_emails_the_owner()
    {
        // A second step added with a borrowed session locks the owner out of
        // password sign-in, so it must not happen silently (#1614).
        const string email = "app-notice@cronus.example";
        var userId = await SeedEmailMfaUserAsync(email);
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).EmailMfaEnabled = false;
            await ctx.SaveChangesAsync();
        }
        var client = _factory.CreateClient();
        using (var loginPage = await client.GetAsync("/login"))
        using (var login = await PostAsync(client, "/auth/login",
                   AntiforgeryToken(await loginPage.Content.ReadAsStringAsync()), ("Email", email), ("Password", Password)))
        {
            login.Headers.Location!.OriginalString.Should().NotContain("err=");
        }
        string secret;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            secret = (await scope.ServiceProvider.GetRequiredService<TotpService>().BeginEnrollmentAsync(userId)).SecretBase32;
        }

        using var security = await client.GetAsync("/account/security");
        var token = AntiforgeryToken(await security.Content.ReadAsStringAsync());
        using (var on = await PostAsync(client, "/auth/account/2fa/totp/confirm", token, ("Code", CurrentCode(secret))))
        {
            on.Headers.Location!.OriginalString.Should().StartWith("/account/2fa/recovery-codes");
        }
        using (var off = await PostAsync(client, "/auth/account/2fa/totp/disable", token, ("Password", Password)))
        {
            off.Headers.Location!.OriginalString.Should().Contain("ok=totp-disabled");
        }

        await using var read = _db.NewContext();
        var notices = await read.EmailOutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ToEmail == email && m.Purpose == EmailPurpose.AuthenticatorAppChanged)
            .OrderBy(m => m.Id)
            .Select(m => m.Subject)
            .ToListAsync();
        notices.Should().Equal(
            ALDevToolbox.Components.Email.AuthenticatorAppChangedEmail.TurnedOnSubject,
            ALDevToolbox.Components.Email.AuthenticatorAppChangedEmail.TurnedOffSubject);
    }

    [Fact]
    public async Task A_day_long_step_up_window_still_asks_for_the_password_after_fifteen_minutes()
    {
        // Turning two-step off or minting recovery codes is as lasting as adding
        // a passkey, so it gets the passkey cap, whatever the organisation chose
        // for its tools (#1614).
        var userId = await SeedPasswordOnlyMicrosoftUserAsync("long-window@cronus.example");
        int previousWindow;
        await using (var ctx = _db.NewContext())
        {
            var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
            previousWindow = org.StepUpWindowMinutes;
            org.StepUpWindowMinutes = 24 * 60;
            await ctx.SaveChangesAsync();
        }
        try
        {
            var now = _factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            (await MayConfirmWithoutPasswordAsync(userId, now.AddMinutes(-5))).Should().BeTrue();
            (await MayConfirmWithoutPasswordAsync(userId, now.AddMinutes(-20)))
                .Should().BeFalse("past the fifteen-minute cap the password is asked for again");
        }
        finally
        {
            await using var ctx = _db.NewContext();
            (await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId)).StepUpWindowMinutes = previousWindow;
            await ctx.SaveChangesAsync();
        }
    }

    private async Task<bool> MayConfirmWithoutPasswordAsync(int userId, DateTime strongAuthAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new(ALDevToolbox.Services.HttpOrganizationContext.UserIdClaim, userId.ToString()),
                new(ALDevToolbox.Services.HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
                new(StepUpAuth.StrongAuthAtClaim, strongAuthAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
            ], "test")),
        };
        var accessor = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = http;
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Data.AppDbContext>();
            return await ALDevToolbox.Endpoints.AccountEndpoints.MayConfirmWithoutPasswordAsync(http, db, userId, CancellationToken.None);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private async Task<int> SeedPasswordOnlyMicrosoftUserAsync(string email)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Nils Consultant",
            PasswordHash = _host.HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        seed.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = user.Id,
            Provider = EntraSignInService.ProviderName,
            Issuer = "11111111-1111-1111-1111-111111111111",
            Subject = "oid-" + user.Id,
            DisplayIdentity = email,
            CreatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(int UserId, string Secret, IReadOnlyList<string> RecoveryCodes)> SeedTotpUserAsync(
        string email, bool linkMicrosoft = false)
    {
        int userId;
        await using (var seed = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                DisplayName = "Nils Consultant",
                PasswordHash = _host.HashPassword(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
            if (linkMicrosoft)
            {
                seed.UserExternalLogins.Add(new UserExternalLogin
                {
                    UserId = userId,
                    Provider = EntraSignInService.ProviderName,
                    Issuer = "11111111-1111-1111-1111-111111111111",
                    Subject = "oid-" + userId,
                    DisplayIdentity = email,
                    CreatedAt = DateTime.UtcNow,
                });
                await seed.SaveChangesAsync();
            }
        }

        // The app's own TOTP service, so the secret is protected with the
        // host's Data Protection keys and the challenge can read it back.
        await using var scope = _factory.Services.CreateAsyncScope();
        var totp = scope.ServiceProvider.GetRequiredService<TotpService>();
        var enrollment = await totp.BeginEnrollmentAsync(userId);
        var codes = await totp.ConfirmEnrollmentAsync(userId, CurrentCode(enrollment.SecretBase32));
        return (userId, enrollment.SecretBase32, codes);
    }

    private async Task<int> SeedEmailMfaUserAsync(string email)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Nils Consultant",
            PasswordHash = _host.HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            EmailMfaEnabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Issues an email code through the app's own service, standing in for the email.</summary>
    private async Task<string> IssueEmailCodeAsync(int userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var code = await scope.ServiceProvider.GetRequiredService<EmailMfaService>().IssueChallengeAsync(userId);
        code.Should().NotBeNull();
        return code!;
    }

    private static string CurrentCode(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

    /// <summary>Signs in with the password and stops at the second-step challenge.</summary>
    private async Task<(HttpClient Client, string Token)> PasswordLegAsync(string email)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = AntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostAsync(client, "/auth/login", token, ("Email", email), ("Password", Password));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().StartWith("/login/challenge");
        return (client, token);
    }

    private async Task<HttpClient> SignInWithRecoveryCodeAsync(string email, string recoveryCode)
    {
        var (client, token) = await PasswordLegAsync(email);
        using var done = await PostAsync(client, "/auth/login/challenge/recovery", token, ("Code", recoveryCode));
        done.StatusCode.Should().Be(HttpStatusCode.Redirect);
        done.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
