using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// An access token hands an assistant the same tools an approved permission
/// screen does, and can outlive the session with no expiry, so creating one
/// asks for what the consent asks for: a recent second factor whenever the
/// organisation has marked any tool for step-up (#1611). The AI assistants tab
/// sends a stale session to step up before it opens the form, and the create
/// POST checks again. See ".design/auth-and-audit.md", "Step-up for sensitive
/// tools", and <see cref="ALDevToolbox.Tests.OAuth.OAuthConsentStepUpTests"/>.
///
/// <para>The cases share one host. The tools marked for step-up are
/// organisation-wide, so each test works in an organisation of its own, with a
/// user of its own.</para>
/// </summary>
public sealed class AccessTokenStepUpTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";
    private const string CreatePath = "/auth/account/access-tokens/create";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _email = SharedEndpointHost.UniqueEmail("pat-step-up");

    /// <summary>This test's own organisation.</summary>
    private readonly int _orgId;

    public AccessTokenStepUpTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
        _orgId = host.SeedOrganization();
    }

    [Fact]
    public async Task With_nothing_marked_a_password_session_creates_a_token()
    {
        var userId = await SeedUserAsync();
        using var client = await SignInWithPasswordAsync();
        var token = await PageTokenAsync(client);

        using var response = await CreateAsync(client, token);

        response.Headers.Location!.OriginalString.Should().Be("/account/access-tokens/created");
        (await TokenCountAsync(userId)).Should().Be(1);
    }

    [Fact]
    public async Task With_a_tool_marked_a_stale_session_is_sent_to_step_up_and_gets_no_token()
    {
        var userId = await SeedUserAsync();
        using var client = await SignInWithPasswordAsync();
        // The form rendered before the admin marked the tool, or sat open past the window.
        var token = await PageTokenAsync(client);
        await MarkStepUpAsync(ToolKey.Releases);

        using var response = await CreateAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(
            StepUpEndpoints.Url(AccountPasskeyEndpoints.CreateTokenAfterStepUpUrl));
        (await TokenCountAsync(userId)).Should().Be(0);
    }

    [Fact]
    public async Task With_a_tool_marked_a_session_that_just_confirmed_a_second_step_creates_a_token()
    {
        var userId = await SeedUserAsync();
        var recoveryCodes = await EnrolTotpAsync(userId);
        using var client = await SignInWithRecoveryCodeAsync(recoveryCodes[0]);
        await MarkStepUpAsync(ToolKey.Releases);
        var token = await PageTokenAsync(client);

        using var response = await CreateAsync(client, token);

        response.Headers.Location!.OriginalString.Should().Be("/account/access-tokens/created");
        (await TokenCountAsync(userId)).Should().Be(1);
    }

    [Fact]
    public async Task With_a_tool_marked_the_tab_sends_a_stale_session_through_step_up_to_create_a_token()
    {
        await SeedUserAsync();
        using var client = await SignInWithPasswordAsync();
        await MarkStepUpAsync(ToolKey.Releases);

        using var page = await client.GetAsync("/account/ai");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain(
            "href=\"" + StepUpEndpoints.Url(AccountPasskeyEndpoints.CreateTokenAfterStepUpUrl) + "\"");
    }

    [Fact]
    public async Task With_nothing_marked_the_tab_does_not_ask_for_step_up()
    {
        await SeedUserAsync();
        using var client = await SignInWithPasswordAsync();

        using var page = await client.GetAsync("/account/ai");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().NotContain(StepUpEndpoints.Path + "?return=");
    }

    private Task<HttpResponseMessage> CreateAsync(HttpClient client, string token) =>
        PostFormAsync(client, CreatePath, token, ("Name", "Cursor on laptop"), ("TtlDays", "0"));

    private async Task<int> TokenCountAsync(int userId)
    {
        await using var read = _db.NewContext();
        return await read.PersonalAccessTokens.IgnoreQueryFilters().CountAsync(p => p.UserId == userId);
    }

    private async Task MarkStepUpAsync(params ToolKey[] tools)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.FirstAsync(o => o.Id == _orgId);
        org.StepUpTools = ToolCatalog.Format(tools);
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedUserAsync()
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = _orgId,
            Email = _email,
            DisplayName = "Nils Consultant",
            PasswordHash = _host.HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }

    private async Task<IReadOnlyList<string>> EnrolTotpAsync(int userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var totp = scope.ServiceProvider.GetRequiredService<TotpService>();
        var enrollment = await totp.BeginEnrollmentAsync(userId);
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(enrollment.SecretBase32)).ComputeTotp(DateTime.UtcNow);
        return await totp.ConfirmEnrollmentAsync(userId, code);
    }

    private async Task<HttpClient> SignInWithPasswordAsync()
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostFormAsync(client, "/auth/login", token, ("Email", _email), ("Password", Password));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    /// <summary>Signs in with the password and a recovery code, which makes the session fresh.</summary>
    private async Task<HttpClient> SignInWithRecoveryCodeAsync(string recoveryCode)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostFormAsync(client, "/auth/login", token, ("Email", _email), ("Password", Password));
        login.Headers.Location!.OriginalString.Should().StartWith("/login/challenge");
        using var done = await PostFormAsync(client, "/auth/login/challenge/recovery", token, ("Code", recoveryCode));
        done.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> PageTokenAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/account/security");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return FormToken(await page.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string FormToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
