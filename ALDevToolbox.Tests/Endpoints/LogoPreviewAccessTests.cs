using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Editors reach the Defaults page that shows the organisation's logo and can
/// change it there, so the preview that page loads must answer them too
/// (#1615, GEN4-2). A plain User still cannot read it.
/// </summary>
public sealed class LogoPreviewAccessTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string Preview = "/admin/configuration/logo/preview";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_editor_can_read_the_logo_preview()
    {
        await SeedUserAsync("erik@cronus.example", UserRole.Editor);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, "erik@cronus.example");

        using var response = await client.GetAsync(Preview);

        // No logo is set, so the endpoint itself answers 404; a role refusal
        // would redirect to the access-denied page instead.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_user_cannot_read_the_logo_preview()
    {
        await SeedUserAsync("mel@cronus.example", UserRole.User);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, "mel@cronus.example");

        using var response = await client.GetAsync(Preview);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
    }

    private async Task SeedUserAsync(string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory, string email)
    {
        var client = factory.CreateClient();
        using var page = await client.GetAsync("/login");
        var token = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }
}
