using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Persistence round-trips for the tool toggles: the SiteAdmin disabled set on
/// <c>system_settings</c> (and its refresh of the in-memory
/// <see cref="ToolAvailabilityState"/>) and the per-org disabled set on
/// <c>organizations</c>.
/// </summary>
public sealed class ToolToggleServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private SystemSettingsService NewSettings(AppDbContext ctx, ToolAvailabilityState? tools = null) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance,
            TimeProvider.System, _db.McpAvailability, tools);

    private static SystemSettingsInput InputWithDisabled(IReadOnlyList<ToolKey> disabled) => new(
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
        ReleaseDownloadDomainAllowlist: null,
        DisabledTools: disabled);

    [Fact]
    public async Task Site_disabled_tools_default_empty()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var view = await NewSettings(ctx).GetViewAsync();

        view.DisabledTools.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_persists_site_disabled_tools_and_refreshes_the_singleton()
    {
        var tools = new ToolAvailabilityState(_db.McpAvailability);

        await using (var saveCtx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await NewSettings(saveCtx, tools).SaveAsync(
                InputWithDisabled(new[] { ToolKey.Projects, ToolKey.Pipelines }));
        }

        // Persisted...
        await using var readCtx = _db.NewContext();
        var view = await NewSettings(readCtx).GetViewAsync();
        ToolCatalog.ParseDisabled(view.DisabledTools)
            .Should().BeEquivalentTo(new[] { ToolKey.Projects, ToolKey.Pipelines });

        // ...and the in-memory singleton was refreshed on save.
        tools.IsSiteEnabled(ToolKey.Projects).Should().BeFalse();
        tools.IsSiteEnabled(ToolKey.Piper).Should().BeTrue();
    }

    [Fact]
    public async Task Save_strips_mcp_from_the_disabled_set()
    {
        await using (var saveCtx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            // A forged set including Mcp must not land in disabled_tools — MCP is
            // owned by McpEnabled.
            await NewSettings(saveCtx).SaveAsync(
                InputWithDisabled(new[] { ToolKey.Mcp, ToolKey.Cookbook }));
        }

        await using var readCtx = _db.NewContext();
        var view = await NewSettings(readCtx).GetViewAsync();
        view.DisabledTools.Should().NotContain(nameof(ToolKey.Mcp));
        view.DisabledTools.Should().Contain(nameof(ToolKey.Cookbook));
    }

    [Fact]
    public async Task Org_set_disabled_tools_round_trips()
    {
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await _db.NewOrganizationAdminService(ctx)
                .SetDisabledToolsAsync(new[] { ToolKey.Translator });
        }

        await using var readCtx = _db.NewContext();
        var stored = await readCtx.Organizations
            .AsNoTracking()
            .Where(o => o.Id == TestDb.DefaultOrgId)
            .Select(o => o.DisabledTools)
            .FirstAsync();

        ToolCatalog.ParseDisabled(stored).Should().BeEquivalentTo(new[] { ToolKey.Translator });
    }

    [Fact]
    public async Task Org_set_disabled_tools_excludes_mcp()
    {
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await _db.NewOrganizationAdminService(ctx)
                .SetDisabledToolsAsync(new[] { ToolKey.Mcp, ToolKey.Piper });
        }

        await using var readCtx = _db.NewContext();
        var stored = await readCtx.Organizations
            .AsNoTracking()
            .Where(o => o.Id == TestDb.DefaultOrgId)
            .Select(o => o.DisabledTools)
            .FirstAsync();

        stored.Should().NotContain(nameof(ToolKey.Mcp), "MCP is toggled through SetMcpEnabledAsync");
        stored.Should().Contain(nameof(ToolKey.Piper));
    }

    [Fact]
    public async Task Org_step_up_tools_round_trip_and_keep_mcp()
    {
        await using var ctx = _db.NewContext();
        // Changing the rule needs a fresh session (StepUpEnforcementTests has the refusal).
        var svc = _db.NewOrganizationAdminService(ctx, TestDb.RequestWithStrongAuth(DateTime.UtcNow));

        (await svc.GetToolsViewAsync()).StepUpTools.Should().BeEmpty();

        await svc.SetStepUpToolsAsync(new[] { ToolKey.Releases, ToolKey.Mcp, ToolKey.Releases });

        var view = await svc.GetToolsViewAsync();
        view.StepUpTools.Should().BeEquivalentTo(new[] { ToolKey.Releases, ToolKey.Mcp },
            "duplicates collapse and MCP is allowed in this set, unlike the disabled one");
        view.DisabledTools.Should().BeEmpty("the two sets are independent");
    }
}
