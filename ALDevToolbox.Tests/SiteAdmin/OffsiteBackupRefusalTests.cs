using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Offsite;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// The security refusals in <see cref="OffsiteBackupService"/> — traversal
/// guards on the DB-sourced file names (#480) and the suspicious-key / wrong-suffix
/// / malformed-key rejections on the download paths (#484) — all fire *before*
/// any storage provider is created. These tests pin that: each refusal throws
/// <see cref="InvalidOperationException"/> and the provider factory is never
/// touched (it throws if it is, proving nothing left the process).
/// </summary>
public sealed class OffsiteBackupRefusalTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ---- #480: upload paths must not trust DB-sourced file names ------------

    [Fact]
    public async Task Upload_refuses_a_tampered_whole_db_file_name()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);
        var row = new Backup
        {
            FileName = "../../keys/dp-key.xml",
            FileSizeBytes = 1,
            CreatedAt = DateTime.UtcNow,
            Kind = BackupKind.Scheduled,
        };
        ctx.Backups.Add(row);
        await ctx.SaveChangesAsync();

        var act = () => NewService(ctx).UploadAsync(row.Id, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*suspicious backup file name*");
    }

    [Fact]
    public async Task Upload_per_tenant_refuses_a_tampered_file_name()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);
        // The "Other" org (slug "other") is seeded by the fixture.
        var row = new PerTenantBackup
        {
            OrganizationId = TestDb.OtherOrgId,
            FileName = @"..\..\keys\dp-key.xml",
            FileSizeBytes = 1,
            CreatedAt = DateTime.UtcNow,
            Kind = BackupKind.Scheduled,
        };
        ctx.PerTenantBackups.Add(row);
        await ctx.SaveChangesAsync();

        var act = () => NewService(ctx).UploadPerTenantAsync(row.Id, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*suspicious snapshot file name*");
    }

    // ---- #484: download paths reject hostile object keys -------------------

    [Fact]
    public async Task Download_refuses_a_suspicious_leaf_name()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);

        var act = () => NewService(ctx).DownloadAsync("../../etc/passwd", progress: null, recoverFromAnotherInstallation: false, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*suspicious leaf name*");
    }

    [Fact]
    public async Task Download_refuses_a_non_dump_object()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);

        var act = () => NewService(ctx).DownloadAsync("not-a-dump.txt", progress: null, recoverFromAnotherInstallation: false, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*non-dump object*");
    }

    [Fact]
    public async Task Download_per_tenant_refuses_a_malformed_key()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);

        // No slug/filename split under the tenants/ prefix.
        var act = () => NewService(ctx).DownloadPerTenantAsync("tenants/onlyslug", progress: null, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*malformed key*");
    }

    [Fact]
    public async Task Download_per_tenant_refuses_a_suspicious_leaf_name()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);

        var act = () => NewService(ctx).DownloadPerTenantAsync("tenants/other/../evil.zip", progress: null, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*suspicious leaf name*");
    }

    [Fact]
    public async Task Download_per_tenant_refuses_a_wrong_suffix()
    {
        await using var ctx = _db.NewContext();
        await ConfigureOffsiteAsync(ctx);

        var act = () => NewService(ctx).DownloadPerTenantAsync("tenants/other/notasnapshot.txt", progress: null, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*non-per-tenant-snapshot object*");
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>
    /// Saves a valid, enabled off-site configuration so <c>ResolveOffsiteAsync</c>
    /// returns non-null and the refusal logic (which runs after that early return)
    /// is actually reached.
    /// </summary>
    private async Task ConfigureOffsiteAsync(AppDbContext ctx)
    {
        var settings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await settings.SaveOffsiteAsync(new OffsiteSettingsInput(
            Enabled: true,
            Provider: null,
            Endpoint: null,
            Region: "eu-west-1",
            Bucket: "cronus-backups",
            Prefix: null,
            AccessKey: "access",
            ClearAccessKey: false,
            SecretKey: "secret",
            ClearSecretKey: false,
            ForcePathStyle: false,
            RetentionDays: 30));
    }

    private OffsiteBackupService NewService(AppDbContext ctx)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString,
            })
            .Build();
        var backups = new BackupService(
            ctx, _db.OrgContext, new MaintenanceModeState(), config,
            NullLogger<BackupService>.Instance, TimeProvider.System);
        var perTenant = new PerTenantBackupService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), config,
            NullLogger<PerTenantBackupService>.Instance, TimeProvider.System);
        var systemSettings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        // A throwaway persistent identity in a scratch dir — the refusals never
        // reach the provenance check that reads it.
        var deployment = DeploymentIdentity.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "aldt-test-deploy-" + Guid.NewGuid().ToString("N")),
            NullLogger.Instance);
        return new OffsiteBackupService(
            ctx, systemSettings, backups, perTenant,
            new ThrowingProviderFactory(),
            NullLogger<OffsiteBackupService>.Instance, TimeProvider.System, deployment);
    }

    /// <summary>
    /// Provider factory that fails loudly if a refusal ever gets far enough to
    /// build a storage provider — the whole point of these tests is that it
    /// doesn't.
    /// </summary>
    private sealed class ThrowingProviderFactory : IOffsiteStorageProviderFactory
    {
        public IOffsiteStorageProvider Create(ResolvedOffsiteSettings settings) =>
            throw new InvalidOperationException(
                "Provider must not be created — the request should have been refused first.");
    }
}
