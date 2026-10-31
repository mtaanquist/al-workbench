using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The New Extension download through the real host when the extension joins
/// a workspace (#1239). The page posts the saved settings it was filled in
/// from, and the endpoint has to read them back the way the "Add to
/// repository" path does: rewrite the settings with the new extension listed,
/// and refuse an ID range one of the saved extensions already uses.
/// </summary>
public sealed class GenerateExtensionEndpointTests : IDisposable
{
    private const string AlexEmail = "alex@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Joining_a_workspace_writes_its_settings_back_with_the_new_extension_listed()
    {
        var savedConfig = await SeedAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await PostAsync(client, savedConfig, idFrom: 92000, idTo: 92999);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using var archive = new ZipArchive(
            new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var settings = archive.GetEntry(WorkspaceConfigService.FileName);
        settings.Should().NotBeNull("the workspace's own settings are rewritten so the next extension knows this one");
        using var reader = new StreamReader(settings!.Open());
        var toml = await reader.ReadToEndAsync();
        toml.Should().Contain("Banking").And.Contain("92000");
    }

    [Fact]
    public async Task Joining_a_workspace_refuses_an_id_range_a_saved_extension_already_uses()
    {
        var savedConfig = await SeedAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        // Core was saved with 90000-90999.
        using var response = await PostAsync(client, savedConfig, idFrom: 90500, idTo: 91499);

        response.Content.Headers.ContentType!.MediaType.Should().NotBe("application/zip");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Overlaps the IDs");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string savedConfig, int idFrom, int idTo)
    {
        var token = await TokenFromAsync(client, "/notifications");
        return await client.PostAsync("/generate/extension", new FormUrlEncodedContent(
        [
            new("TemplateKey", "runtime-test"),
            new("ExtensionName", "Banking"),
            new("Brief", "Bank integration."),
            new("Description", "Bank integration."),
            new("ApplicationVersion", "24.0.0.0"),
            new("RuntimeVersion", "15"),
            new("IdRangeFrom", idFrom.ToString()),
            new("IdRangeTo", idTo.ToString()),
            new("IncludeExamples", "false"),
            new("WorkspaceConfig", savedConfig),
            new("__RequestVerificationToken", token),
        ]));
    }

    /// <summary>Seeds the template and the user, and returns the settings a generated workspace saved.</summary>
    private async Task<string> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default());
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AlexEmail,
            DisplayName = "Alex Hansen",
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        await _db.NewOrganizationConfigService(ctx).SaveSettingsAsync(
            new ALDevToolbox.Services.Organizations.OrganizationSettingsInput("CRONUS", 50000, 99999, string.Empty, string.Empty));

        var plan = PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Denmark", extensionPrefix: "CRO");
        return new WorkspaceConfigService(ctx).BuildWorkspace(plan,
        [
            new WorkspaceExtensionIdentity(
                WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "CRO Core", "Core", "CRONUS", 90000, 90999),
        ]);
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", AlexEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
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
