using System.Net;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// SiteAdmin runtime toggle for the MCP server. The
/// <see cref="SystemSettingsService.IsMcpEnabledAsync"/> helper is what
/// the NavMenu and the <c>/mcp</c> endpoint filter both call; the
/// end-to-end test boots the real app and verifies <c>/mcp</c> returns
/// 404 when the toggle is off, regardless of whether the caller has a
/// valid PAT.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class McpEnabledToggleTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task IsMcpEnabledAsync_defaults_to_false_for_fresh_install()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);

        (await svc.IsMcpEnabledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Save_flips_McpEnabled_and_subsequent_reads_see_it()
    {
        await using (var saveCtx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            var saveSvc = new SystemSettingsService(saveCtx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
            await saveSvc.SaveAsync(new SystemSettingsInput(
                SmtpHost: null, SmtpPort: null, SmtpUser: null,
                SmtpPassword: null, ClearSmtpPassword: false,
                SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
                BackupScheduleEnabled: true,
                BackupScheduleTimeUtc: new TimeOnly(2, 0),
                BackupRetentionCount: 14,
                PerTenantBackupRetentionCount: 30,
                DefaultStorageQuotaMb: null,
                IndexSizeMultiplier: 0.5m,
                McpEnabled: true,
                SignupEmailDomainAllowlist: null,
                ReleaseDownloadDomainAllowlist: null, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
        }

        await using var readCtx = _db.NewContext();
        var readSvc = new SystemSettingsService(readCtx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);

        (await readSvc.IsMcpEnabledAsync()).Should().BeTrue();
        var view = await readSvc.GetViewAsync();
        view.McpEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Mcp_endpoint_returns_404_when_site_admin_toggle_is_off()
    {
        // Defaults to off in a fresh database — no save needed.
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        var pat = await IssuePatAsync(factory.Services);
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"the SiteAdmin toggle defaults to false, so /mcp should not respond. Body was: {body}");
    }

    [Fact]
    public async Task Mcp_endpoint_accepts_valid_pat_once_toggle_is_on()
    {
        await EnableMcpAsync();

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        var pat = await IssuePatAsync(factory.Services);
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "with the toggle on, the route should respond — auth/validation may still fail with 400/415 but not 404");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "a valid PAT should authenticate against the bearer scheme");
    }

    [Fact]
    public async Task Mcp_endpoint_rejects_missing_authorization_header()
    {
        await EnableMcpAsync();

        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/mcp", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- helpers -----------------------------------------------------------

    private async Task EnableMcpAsync()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await svc.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: true,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: null, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
    }

    private async Task<string> IssuePatAsync(IServiceProvider services)
    {
        // Use a fresh context bound to the test database so we can seed a
        // user and a token without going through the real signup flow.
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var user = new Domain.Entities.User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"mcp-toggle-{Guid.NewGuid():N}@example.com",
            DisplayName = "MCP Toggle Tester",
            PasswordHash = "ignored",
            Role = Domain.Entities.UserRole.Admin,
            Status = Domain.Entities.UserStatus.Active,
            CreatedAt = now,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var svc = new PersonalAccessTokenService(ctx, TimeProvider.System, NullLogger<PersonalAccessTokenService>.Instance);
        var issued = await svc.IssueAsync(user.Id, user.OrganizationId, "test", expiresAt: null);
        return issued.Plaintext;
    }
}
