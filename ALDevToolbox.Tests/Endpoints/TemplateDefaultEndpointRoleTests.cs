using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// GEN2-2: choosing the organisation's default template is Admin-only. The
/// templates list hides "Set as default" from an Editor; these pin the
/// endpoint behind it, so an Editor's hand-posted form is refused and
/// changes nothing, while the same post from an Admin goes through.
/// </summary>
public sealed class TemplateDefaultEndpointRoleTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string AdminEmail = "ada@cronus.example";
    private const string EditorEmail = "erik@cronus.example";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_editor_with_a_valid_token_cannot_set_the_default_template()
    {
        await SeedUserAsync(EditorEmail, UserRole.Editor);
        var templateId = await SeedTemplateAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, EditorEmail);
        var token = await TokenFromAsync(client, "/notifications");

        using var response = await client.PostAsync($"/admin/templates/{templateId}/default",
            Form(("__RequestVerificationToken", token)));

        // The cookie handler answers a role refusal by sending the browser to
        // its access-denied page (/login) rather than a bare 403.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            response.Headers.Location!.AbsolutePath.Should().Be("/login");
        }
        (await IsDefaultAsync(templateId)).Should().BeFalse(because: "an Editor may not choose the default template");
    }

    [Fact]
    public async Task An_admin_with_a_valid_token_can_set_the_default_template()
    {
        // The control for the test above: the refusal there is the role, not the token.
        await SeedUserAsync(AdminEmail, UserRole.Admin);
        var templateId = await SeedTemplateAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AdminEmail);
        var token = await TokenFromAsync(client, "/notifications");

        using var response = await client.PostAsync($"/admin/templates/{templateId}/default",
            Form(("__RequestVerificationToken", token)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("ok=default-set");
        (await IsDefaultAsync(templateId)).Should().BeTrue();
    }

    // ---- helpers -----------------------------------------------------------

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<int> SeedTemplateAsync()
    {
        await using var ctx = _db.NewContext();
        var template = TemplateBuilder.Default("cronus-runtime", organizationId: TestDb.DefaultOrgId);
        template.IsDefault = false;
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
        return template.Id;
    }

    private async Task<bool> IsDefaultAsync(int templateId)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.RuntimeTemplates.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == templateId)).IsDefault;
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
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", Form(
            ("Email", email), ("Password", Password), ("__RequestVerificationToken", token)));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
