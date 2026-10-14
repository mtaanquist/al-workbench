using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Offsite;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Which deployment an off-site object belongs to, when two deployments share a
/// bucket and prefix: the per-tenant stamp and its checks (#1257), and the
/// retention prune, which must only ever delete this deployment's objects and
/// must prune per-tenant snapshots too (#1258). Runs against an in-memory
/// storage provider so the prune's choices can be read back exactly.
/// </summary>
public sealed class OffsiteBackupProvenanceTests : IDisposable
{
    private const int RetentionDays = 7;

    private readonly TestDb _db = new();
    private readonly string _root;
    private readonly BackupOptions _options;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));
    private readonly InMemoryProvider _store = new();
    private readonly DeploymentIdentity _us;
    private readonly DeploymentIdentity _neighbour;

    public OffsiteBackupProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aldt-offsite-provenance-" + Guid.NewGuid().ToString("N"));
        _options = new BackupOptions { Directory = Path.Combine(_root, "backups") };
        Directory.CreateDirectory(_options.Directory);
        _us = DeploymentIdentity.LoadOrCreate(Path.Combine(_root, "us"), NullLogger.Instance);
        _neighbour = DeploymentIdentity.LoadOrCreate(Path.Combine(_root, "neighbour"), NullLogger.Instance);
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ---- #1258: prune --------------------------------------------------------

    [Fact]
    public async Task Prune_deletes_only_this_deployments_expired_dumps_and_keeps_its_own_newest_three()
    {
        await ConfigureOffsiteAsync();
        PutDump("ours-1", _us, Now.AddDays(-1));
        PutDump("ours-10", _us, Now.AddDays(-10));
        PutDump("ours-11", _us, Now.AddDays(-11));
        PutDump("ours-12", _us, Now.AddDays(-12));
        // The neighbour's three newest are newer than any of ours; they must not
        // take our keep-newest-three places, and its expired dump is not ours to delete.
        PutDump("theirs-0a", _neighbour, Now.AddHours(-1));
        PutDump("theirs-0b", _neighbour, Now.AddHours(-2));
        PutDump("theirs-0c", _neighbour, Now.AddHours(-3));
        PutDump("theirs-30", _neighbour, Now.AddDays(-30));

        await PruneAsync();

        _store.Keys.Should().BeEquivalentTo(
            DumpKey("ours-1"), DumpKey("ours-10"), DumpKey("ours-11"),
            DumpKey("theirs-0a"), DumpKey("theirs-0b"), DumpKey("theirs-0c"), DumpKey("theirs-30"));
    }

    [Fact]
    public async Task Prune_treats_unstamped_dumps_as_ours_when_no_neighbour_shares_the_prefix()
    {
        await ConfigureOffsiteAsync();
        PutDump("legacy-20", null, Now.AddDays(-20));
        PutDump("legacy-21", null, Now.AddDays(-21));
        PutDump("legacy-22", null, Now.AddDays(-22));
        PutDump("legacy-23", null, Now.AddDays(-23));

        await PruneAsync();

        _store.Keys.Should().BeEquivalentTo(DumpKey("legacy-20"), DumpKey("legacy-21"), DumpKey("legacy-22"));
    }

    [Fact]
    public async Task Prune_leaves_unstamped_objects_alone_when_a_neighbour_shares_the_prefix()
    {
        await ConfigureOffsiteAsync();
        PutDump("theirs-1", _neighbour, Now.AddDays(-1));
        PutDump("legacy-20", null, Now.AddDays(-20));
        PutDump("legacy-21", null, Now.AddDays(-21));
        PutDump("legacy-22", null, Now.AddDays(-22));
        PutDump("legacy-23", null, Now.AddDays(-23));
        PutSnapshot("default", "legacy-snap", null, Now.AddDays(-40));

        await PruneAsync();

        _store.Keys.Should().HaveCount(6, "nothing unstamped can be told apart from the neighbour's");
    }

    [Fact]
    public async Task Prune_deletes_expired_per_tenant_snapshots_per_organisation_and_keeps_each_ones_newest_three()
    {
        await ConfigureOffsiteAsync();
        PutSnapshot("default", "d-1", _us, Now.AddDays(-1));
        PutSnapshot("default", "d-20", _us, Now.AddDays(-20));
        PutSnapshot("default", "d-21", _us, Now.AddDays(-21));
        PutSnapshot("default", "d-22", _us, Now.AddDays(-22));
        PutSnapshot("default", "d-legacy-30", null, Now.AddDays(-30));
        PutSnapshot("default", "d-theirs-40", _neighbour, Now.AddDays(-40));
        PutSnapshot("other", "o-20", _us, Now.AddDays(-20));
        PutSnapshot("other", "o-21", _us, Now.AddDays(-21));
        // A dump keeps the prefix "ours alone" for the unstamped legacy snapshot.
        PutDump("ours-1", _us, Now.AddDays(-1));

        await PruneAsync();

        _store.Keys.Should().BeEquivalentTo(
            SnapshotKey("default", "d-1"), SnapshotKey("default", "d-20"), SnapshotKey("default", "d-21"),
            SnapshotKey("default", "d-theirs-40"),
            SnapshotKey("other", "o-20"), SnapshotKey("other", "o-21"),
            DumpKey("ours-1"));
    }

    [Fact]
    public async Task Prune_does_not_list_the_per_tenant_subtree_for_the_database_backups()
    {
        await ConfigureOffsiteAsync();
        PutDump("ours-1", _us, Now.AddDays(-1));
        PutSnapshot("default", "d-1", _us, Now.AddDays(-1));

        await PruneAsync();

        _store.Listings.Should().Contain(("", true));
        _store.Listings.Should().NotContain(("", false), "a recursive listing of the whole prefix pages through every snapshot");
    }

    // ---- #1257: per-tenant stamp --------------------------------------------

    [Fact]
    public async Task Per_tenant_upload_is_stamped_in_its_metadata_and_its_manifest()
    {
        await ConfigureOffsiteAsync();
        var row = await CreateSnapshotAsync(_us);

        var key = await NewOffsite(_us).UploadPerTenantAsync(row.Id, default);

        key.Should().NotBeNull();
        _store.Metadata(key!)[OffsiteBackupService.DeploymentMetadataKey].Should().Be(_us.Id);
        ReadManifest(_store.Body(key!)).deployment_id.Should().Be(_us.Id);
    }

    [Fact]
    public async Task Per_tenant_download_refuses_a_snapshot_stamped_by_another_deployment()
    {
        await ConfigureOffsiteAsync();
        PutSnapshot("default", "theirs", _neighbour, Now.AddDays(-1));

        var act = () => NewOffsite(_us).DownloadPerTenantAsync(SnapshotKey("default", "theirs"), progress: null, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different deployment*");
        Directory.Exists(Path.Combine(_options.Directory, "tenants", "default")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_options.Directory, "tenants", "default")).Should().BeEmpty();
    }

    [Fact]
    public async Task Per_tenant_download_refuses_an_unstamped_object_whose_manifest_names_another_deployment()
    {
        await ConfigureOffsiteAsync();
        var theirs = await CreateSnapshotAsync(_neighbour);
        var key = SnapshotKey("default", "copied");
        _store.Put(key, await File.ReadAllBytesAsync(SnapshotPath(theirs)), metadata: null, Now);

        var act = () => NewOffsite(_us).DownloadPerTenantAsync(key, progress: null, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*manifest names a different deployment*");
    }

    [Fact]
    public async Task Per_tenant_download_accepts_this_deployments_own_snapshot()
    {
        await ConfigureOffsiteAsync();
        var row = await CreateSnapshotAsync(_us);
        var key = await NewOffsite(_us).UploadPerTenantAsync(row.Id, default);
        File.Delete(SnapshotPath(row));
        await using (var ctx = _db.NewContext())
        {
            await ctx.PerTenantBackups.Where(b => b.Id == row.Id).ExecuteDeleteAsync();
        }

        var id = await NewOffsite(_us).DownloadPerTenantAsync(key!, progress: null, default);

        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Restore_refuses_a_snapshot_whose_manifest_names_another_deployment()
    {
        var theirs = await CreateSnapshotAsync(_neighbour);

        await using var ctx = _db.NewContext();
        var act = () => NewPerTenant(ctx, _us).RestoreAsync(theirs.Id);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("BackupId");
    }

    [Fact]
    public async Task Per_tenant_listing_flags_another_deployments_snapshots()
    {
        await ConfigureOffsiteAsync();
        PutSnapshot("default", "ours", _us, Now.AddDays(-1));
        PutSnapshot("default", "theirs", _neighbour, Now.AddDays(-2));
        PutSnapshot("default", "legacy", null, Now.AddDays(-3));

        var listed = await NewOffsite(_us).ListPerTenantAsync(maxPerOrganization: 50, flagOtherDeployments: true, default);

        listed.ToDictionary(o => o.FileName, o => o.FromAnotherDeployment).Should().BeEquivalentTo(
            new Dictionary<string, bool>
            {
                ["default-ours" + PerTenantBackupService.FileSuffix] = false,
                ["default-theirs" + PerTenantBackupService.FileSuffix] = true,
                ["default-legacy" + PerTenantBackupService.FileSuffix] = false,
            });
    }

    // ---- #1279: whole-database dump provenance and the manifest slug ----------

    [Fact]
    public async Task Dump_download_refuses_a_dump_stamped_by_another_deployment()
    {
        await ConfigureOffsiteAsync();
        PutDump("theirs", _neighbour, Now.AddDays(-1));

        var act = () => NewOffsite(_us).DownloadAsync(DumpKey("theirs"), progress: null, recoverFromAnotherInstallation: false, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different deployment*");
        Directory.GetFiles(_options.Directory).Should().BeEmpty("nothing of another deployment's may land here");
        await using var ctx = _db.NewContext();
        (await ctx.Backups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dump_download_accepts_an_unstamped_legacy_dump()
    {
        await ConfigureOffsiteAsync();
        PutDump("legacy", null, Now.AddDays(-1));

        var id = await NewOffsite(_us).DownloadAsync(DumpKey("legacy"), progress: null, recoverFromAnotherInstallation: false, default);

        await using var ctx = _db.NewContext();
        var row = await ctx.Backups.AsNoTracking().SingleAsync(b => b.Id == id);
        row.OffsiteObjectKey.Should().Be(DumpKey("legacy"));
        File.Exists(Path.Combine(_options.Directory, row.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task Per_tenant_download_refuses_a_snapshot_whose_manifest_names_another_organisation()
    {
        await ConfigureOffsiteAsync();
        var ours = await CreateSnapshotAsync(_us);
        var key = SnapshotKey("other", "moved");
        _store.Put(key, await File.ReadAllBytesAsync(SnapshotPath(ours)), Stamp(_us), Now);

        var act = () => NewOffsite(_us).DownloadPerTenantAsync(key, progress: null, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*manifest names org 'default'*");
        Directory.GetFiles(Path.Combine(_options.Directory, "tenants", "other"))
            .Should().BeEmpty("the staging copy is deleted along with the refusal");
        await using var ctx = _db.NewContext();
        (await ctx.PerTenantBackups.CountAsync(b => b.OrganizationId == TestDb.OtherOrgId)).Should().Be(0);
    }

    // ---- #1269: a downloaded dump keeps the date in its name -----------------

    [Theory]
    [InlineData("aldevtoolbox-20260830T020000123Z-scheduled.dump", 123)]
    [InlineData("aldevtoolbox-20260830T020000Z-scheduled.dump", 0)]
    public async Task A_downloaded_dump_is_dated_from_its_name_in_either_stamp_shape(string key, int milliseconds)
    {
        await ConfigureOffsiteAsync();
        _store.Put(key, [1], Stamp(_us), Now.AddDays(-2));

        var id = await NewOffsite(_us).DownloadAsync(key, progress: null, recoverFromAnotherInstallation: false, default);

        await using var ctx = _db.NewContext();
        (await ctx.Backups.AsNoTracking().SingleAsync(b => b.Id == id)).CreatedAt
            .Should().Be(new DateTime(2026, 8, 30, 2, 0, 0, milliseconds, DateTimeKind.Utc), "not the moment it was downloaded");
    }

    // ---- #1322: rebuilding a lost installation on a fresh one ----------------

    [Fact]
    public async Task A_fresh_installation_recovers_a_lost_installations_dump_and_takes_over_its_backups()
    {
        await ConfigureOffsiteAsync();
        await MakeFreshAsync();
        PutDump("lost", _neighbour, Now.AddDays(-1));

        var id = await NewOffsite(_us).DownloadAsync(DumpKey("lost"), progress: null, recoverFromAnotherInstallation: true, default);

        await using (var ctx = _db.NewContext())
        {
            (await ctx.Backups.AsNoTracking().SingleAsync(b => b.Id == id)).OffsiteObjectKey.Should().Be(DumpKey("lost"));
        }
        _us.IsForeign(_neighbour.Id).Should().BeFalse("the lost installation's other backups are this one's now");
        DeploymentIdentity.LoadOrCreate(Path.Combine(_root, "us"), NullLogger.Instance).IsOwn(_neighbour.Id)
            .Should().BeTrue("the takeover outlives a restart, and the restore that replaces every table");
    }

    [Fact]
    public async Task After_a_recovery_the_lost_installations_snapshots_are_offered_and_pruned_as_ours()
    {
        await ConfigureOffsiteAsync();
        await MakeFreshAsync();
        PutDump("lost", _neighbour, Now.AddDays(-1));
        await NewOffsite(_us).DownloadAsync(DumpKey("lost"), progress: null, recoverFromAnotherInstallation: true, default);
        PutSnapshot("default", "lost-1", _neighbour, Now.AddDays(-1));
        PutSnapshot("default", "lost-2", _neighbour, Now.AddDays(-2));
        PutSnapshot("default", "lost-3", _neighbour, Now.AddDays(-3));
        PutSnapshot("default", "lost-30", _neighbour, Now.AddDays(-30));

        var listed = await NewOffsite(_us).ListPerTenantAsync(maxPerOrganization: 10, flagOtherDeployments: true, default);
        listed.Should().OnlyContain(o => !o.FromAnotherDeployment);

        await PruneAsync();
        _store.Keys.Should().NotContain(SnapshotKey("default", "lost-30"), "it is past retention and now ours to prune");
    }

    [Fact]
    public async Task Recovering_another_installations_dump_is_refused_once_this_one_has_organisations()
    {
        await ConfigureOffsiteAsync();
        PutDump("theirs", _neighbour, Now.AddDays(-1));

        var act = () => NewOffsite(_us).DownloadAsync(DumpKey("theirs"), progress: null, recoverFromAnotherInstallation: true, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fresh installation*");
        _us.IsForeign(_neighbour.Id).Should().BeTrue("a refused recovery takes nothing over");
        await using var ctx = _db.NewContext();
        (await ctx.Backups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recovering_another_installations_dump_is_refused_once_this_one_has_users()
    {
        await ConfigureOffsiteAsync();
        await MakeFreshAsync();
        await using (var ctx = _db.NewContext())
        {
            foreach (var name in new[] { "admin", "colleague" })
            {
                ctx.Users.Add(new User
                {
                    OrganizationId = TestDb.DefaultOrgId, Email = $"{name}@cronus.example", DisplayName = name,
                    Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = Now,
                });
            }
            await ctx.SaveChangesAsync();
        }
        PutDump("theirs", _neighbour, Now.AddDays(-1));

        await using var offsiteCtx = _db.NewContext();
        var offsite = NewOffsite(_us, offsiteCtx);
        (await offsite.CanRecoverAnotherInstallationAsync(default)).Should().BeFalse();
        var act = () => offsite.DownloadAsync(DumpKey("theirs"), progress: null, recoverFromAnotherInstallation: true, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fresh installation*");
    }

    [Fact]
    public async Task The_database_catalogue_flags_another_installations_dumps()
    {
        await ConfigureOffsiteAsync();
        PutDump("ours", _us, Now.AddDays(-1));
        PutDump("theirs", _neighbour, Now.AddDays(-2));

        var listed = await NewOffsite(_us).ListAsync(maxObjects: 10, flagOtherDeployments: true, default);

        listed.ToDictionary(o => o.FileName, o => o.FromAnotherDeployment).Should().BeEquivalentTo(
            new Dictionary<string, bool> { [DumpKey("ours")] = false, [DumpKey("theirs")] = true });
    }

    [Fact]
    public void A_trusted_id_must_look_like_a_deployment_id()
    {
        var act = () => _us.Trust("../../etc\npasswd");

        act.Should().Throw<ArgumentException>();
        _us.TrustedFormerIds.Should().BeEmpty();
    }

    // ---- #1333: the catalogues cut after sorting, not before -----------------

    [Fact]
    public async Task The_database_catalogue_keeps_the_newest_dumps_when_it_cuts()
    {
        await ConfigureOffsiteAsync();
        // Names sort oldest first, as the timestamped dump names do.
        for (var day = 1; day <= 30; day++)
        {
            PutDump($"2026{day:00}", _us, Now.AddDays(-31 + day));
        }

        var listed = await NewOffsite(_us).ListAsync(maxObjects: 5, flagOtherDeployments: false, default);

        listed.Select(o => o.FileName).Should().Equal(
            DumpKey("202630"), DumpKey("202629"), DumpKey("202628"), DumpKey("202627"), DumpKey("202626"));
    }

    [Fact]
    public async Task The_tenant_catalogue_shows_every_organisations_newest_snapshots_and_reads_only_their_stamps()
    {
        await ConfigureOffsiteAsync();
        foreach (var slug in new[] { "alpha", "bravo", "charlie" })
        {
            for (var day = 1; day <= 20; day++)
            {
                PutSnapshot(slug, $"2026{day:00}", _us, Now.AddDays(-21 + day));
            }
        }

        var listed = await NewOffsite(_us).ListPerTenantAsync(maxPerOrganization: 2, flagOtherDeployments: true, default);

        listed.Select(o => (o.Slug, o.LastModified)).Should().Equal(
            ("alpha", Now.AddDays(-1)), ("alpha", Now.AddDays(-2)),
            ("bravo", Now.AddDays(-1)), ("bravo", Now.AddDays(-2)),
            ("charlie", Now.AddDays(-1)), ("charlie", Now.AddDays(-2)));
        _store.MetadataReads.Should().HaveCount(6, "only the rows shown need their stamp");
    }

    [Fact]
    public async Task Finding_a_dump_checks_its_shape_and_that_it_exists()
    {
        await ConfigureOffsiteAsync();
        PutDump("there", _us, Now);
        PutSnapshot("default", "snap", _us, Now);
        var offsite = NewOffsite(_us);

        (await offsite.FindDumpAsync(DumpKey("there"), default)).Should().Be(DumpKey("there"));
        (await offsite.FindDumpAsync(DumpKey("gone"), default)).Should().BeNull("there is no such object");
        (await offsite.FindDumpAsync(SnapshotKey("default", "snap"), default)).Should().BeNull("a snapshot is not a dump");
        (await offsite.FindDumpAsync("../" + DumpKey("there"), default)).Should().BeNull();
    }

    [Fact]
    public async Task Finding_a_snapshot_checks_its_shape_and_that_it_exists()
    {
        await ConfigureOffsiteAsync();
        PutDump("there", _us, Now);
        PutSnapshot("default", "snap", _us, Now);
        var offsite = NewOffsite(_us);

        (await offsite.FindPerTenantSnapshotAsync(SnapshotKey("default", "snap"), default))
            .Should().Be($"default-snap{PerTenantBackupService.FileSuffix}");
        (await offsite.FindPerTenantSnapshotAsync(SnapshotKey("default", "gone"), default)).Should().BeNull();
        (await offsite.FindPerTenantSnapshotAsync(DumpKey("there"), default)).Should().BeNull();
    }

    // ---- #1334: the tenant catalogue knows organisations, not snapshot rows --

    [Fact]
    public async Task Organisation_slugs_include_organisations_with_no_local_snapshot()
    {
        await using var ctx = _db.NewContext();

        var slugs = await NewPerTenant(ctx, _us).ListOrganizationSlugsAsync(default);

        slugs.Should().Contain("other", "it has no snapshot here, as after a whole-database restore");
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>The shape of a just-built host: the system org alone.</summary>
    private async Task MakeFreshAsync()
    {
        await using var ctx = _db.NewContext();
        await ctx.Organizations.Where(o => o.Id == TestDb.OtherOrgId).ExecuteDeleteAsync();
    }

    private static string DumpKey(string name) => $"aldevtoolbox-{name}{BackupService.BackupFileSuffix}";

    private static string SnapshotKey(string slug, string name) =>
        $"{OffsiteBackupService.PerTenantKeyPrefix}{slug}/{slug}-{name}{PerTenantBackupService.FileSuffix}";

    private void PutDump(string name, DeploymentIdentity? stampedBy, DateTime modified) =>
        _store.Put(DumpKey(name), [1], Stamp(stampedBy), modified);

    private void PutSnapshot(string slug, string name, DeploymentIdentity? stampedBy, DateTime modified) =>
        _store.Put(SnapshotKey(slug, name), [1], Stamp(stampedBy), modified);

    private static Dictionary<string, string>? Stamp(DeploymentIdentity? by) =>
        by is null ? null : new() { [OffsiteBackupService.DeploymentMetadataKey] = by.Id };

    private async Task PruneAsync()
    {
        await using var ctx = _db.NewContext();
        await NewOffsite(_us, ctx).PruneAsync(default);
    }

    private async Task<PerTenantBackup> CreateSnapshotAsync(DeploymentIdentity by)
    {
        await using var ctx = _db.NewContext();
        var row = await NewPerTenant(ctx, by).CreateAsync(TestDb.DefaultOrgId, BackupKind.AdHoc, default);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return row;
    }

    private string SnapshotPath(PerTenantBackup row) => Path.Combine(_options.Directory, "tenants", "default", row.FileName);

    private static PerTenantBackupManifest ReadManifest(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var stream = zip.GetEntry(PerTenantBackupService.ManifestEntryName)!.Open();
        return JsonSerializer.Deserialize<PerTenantBackupManifest>(stream)!;
    }

    private async Task ConfigureOffsiteAsync()
    {
        await using var ctx = _db.NewContext();
        await NewSystemSettings(ctx).SaveOffsiteAsync(new OffsiteSettingsInput(
            Enabled: true, Provider: null, Endpoint: null, Region: "eu-west-1", Bucket: "cronus-backups",
            Prefix: null, AccessKey: "access", ClearAccessKey: false, SecretKey: "secret", ClearSecretKey: false,
            ForcePathStyle: false, RetentionDays: RetentionDays));
    }

    private SystemSettingsService NewSystemSettings(AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString,
        })
        .Build();

    private PerTenantBackupService NewPerTenant(AppDbContext ctx, DeploymentIdentity deployment) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), Config(),
            NullLogger<PerTenantBackupService>.Instance, _clock, _options, deployment);

    private OffsiteBackupService NewOffsite(DeploymentIdentity deployment, AppDbContext? ctx = null)
    {
        ctx ??= _db.NewContext();
        var backups = new BackupService(
            ctx, _db.OrgContext, new MaintenanceModeState(), Config(),
            NullLogger<BackupService>.Instance, _clock, _options);
        return new OffsiteBackupService(
            ctx, NewSystemSettings(ctx), backups, NewPerTenant(ctx, deployment),
            new Factory(_store), NullLogger<OffsiteBackupService>.Instance, _clock, deployment);
    }

    private sealed class Factory(InMemoryProvider store) : IOffsiteStorageProviderFactory
    {
        public IOffsiteStorageProvider Create(ResolvedOffsiteSettings settings) => store;
    }

    /// <summary>An object store in a dictionary, recording each listing it is asked for.</summary>
    private sealed class InMemoryProvider : IOffsiteStorageProvider
    {
        private sealed record Entry(byte[] Body, IReadOnlyDictionary<string, string> Metadata, DateTime Modified);

        private readonly ConcurrentDictionary<string, Entry> _objects = new(StringComparer.Ordinal);

        public ConcurrentBag<(string Prefix, bool TopLevelOnly)> Listings { get; } = new();

        public IReadOnlyCollection<string> Keys => _objects.Keys.ToList();

        public IReadOnlyDictionary<string, string> Metadata(string key) => _objects[key].Metadata;

        public byte[] Body(string key) => _objects[key].Body;

        public void Put(string key, byte[] body, IReadOnlyDictionary<string, string>? metadata, DateTime modified) =>
            _objects[key] = new Entry(body, metadata ?? new Dictionary<string, string>(), modified);

        public Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct) =>
            Task.FromResult(new OffsiteTestResult(true, "ok"));

        public async Task UploadAsync(string key, Stream content, string contentType,
            IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Put(key, buffer.ToArray(), new Dictionary<string, string>(metadata), DateTime.UtcNow);
        }

        public Task<OffsiteDownload> OpenReadAsync(string key, CancellationToken ct)
        {
            var entry = _objects[key];
            return Task.FromResult(new OffsiteDownload(new MemoryStream(entry.Body), entry.Metadata, entry.Body.Length));
        }

        public ConcurrentBag<string> MetadataReads { get; } = new();

        public Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key, CancellationToken ct)
        {
            MetadataReads.Add(key);
            // Both SDKs throw for a missing object; KeyNotFoundException stands in.
            return Task.FromResult(_objects[key].Metadata);
        }

        public Task<IReadOnlyList<OffsiteStorageObject>> ListAsync(string prefix, bool topLevelOnly, CancellationToken ct)
        {
            Listings.Add((prefix, topLevelOnly));
            // Key order, as S3 and Azure both list.
            IReadOnlyList<OffsiteStorageObject> listed = _objects
                .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(o => !topLevelOnly || !o.Key[prefix.Length..].Contains('/'))
                .OrderBy(o => o.Key, StringComparer.Ordinal)
                .Select(o => new OffsiteStorageObject(o.Key, o.Value.Body.Length, o.Value.Modified))
                .ToList();
            return Task.FromResult(listed);
        }

        public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
        {
            foreach (var key in keys) _objects.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
