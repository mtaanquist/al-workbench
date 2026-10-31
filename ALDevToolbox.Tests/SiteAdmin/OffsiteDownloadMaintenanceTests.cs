using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Offsite;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// The off-site download worker across a restore. It runs on its own scope,
/// with no request and no circuit, so the database guard (#1571) sees a
/// background job: it has to be counted while the site is open, wait while a
/// restore runs, and still get through while a failed restore holds the site
/// closed, since pulling a good backup from the bucket is how the SiteAdmin
/// recovers from that (#1597). Its final insert takes the backup lock, so it
/// cannot land inside a restore (#1579). Each test runs the real worker
/// against a context with the guard installed.
/// </summary>
public sealed class OffsiteDownloadMaintenanceTests : IDisposable
{
    private const string Key = "aldevtoolbox-20260830T020000Z-scheduled" + BackupService.BackupFileSuffix;

    private readonly TestDb _db = new();
    private readonly MaintenanceModeState _maintenance = new();
    private readonly OffsiteRestoreJobs _jobs;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));
    private readonly string _root;
    private readonly BackupOptions _options;
    private readonly DeploymentIdentity _us;
    private readonly OneObjectProvider _store = new();

    public OffsiteDownloadMaintenanceTests()
    {
        _jobs = new OffsiteRestoreJobs(_clock);
        _root = Path.Combine(Path.GetTempPath(), "aldt-offsite-maintenance-" + Guid.NewGuid().ToString("N"));
        _options = new BackupOptions { Directory = Path.Combine(_root, "backups") };
        Directory.CreateDirectory(_options.Directory);
        _us = DeploymentIdentity.LoadOrCreate(Path.Combine(_root, "us"), NullLogger.Instance);
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    [Fact]
    public async Task A_download_runs_while_a_failed_restore_holds_the_site_closed()
    {
        await ConfigureOffsiteAsync();
        _maintenance.HoldAfterFailedRestore("Restoring backup test.dump failed.");

        var job = await RunJobAsync();

        job.Status.Should().Be(OffsiteRestoreJobStatus.Completed, job.Error);
        await using var ctx = _db.NewContext();
        (await ctx.Backups.AsNoTracking().SingleAsync(b => b.Id == job.BackupId, TestContext.Current.CancellationToken))
            .OffsiteObjectKey.Should().Be(Key);
        File.Exists(Path.Combine(_options.Directory, Key)).Should().BeTrue();
        _maintenance.ActiveJobs.Should().Be(0, "a download in the held state must not hold up the restore that follows");
        _maintenance.IsHeldAfterFailedRestore.Should().BeTrue();
    }

    [Fact]
    public async Task A_download_queued_during_a_restore_waits_for_the_site_to_reopen()
    {
        await ConfigureOffsiteAsync();
        _maintenance.Enter("Restoring backup test.dump");
        var worker = NewWorker();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var id = _jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, Key, Key);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            _jobs.Get(id)!.Status.Should().Be(OffsiteRestoreJobStatus.Pending, "nothing may start while a restore runs");

            _maintenance.Exit();
            var job = await WaitForTerminalAsync(id);

            job.Status.Should().Be(OffsiteRestoreJobStatus.Completed, job.Error);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_download_started_while_the_site_is_open_is_counted_until_it_finishes()
    {
        await ConfigureOffsiteAsync();
        _store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = NewWorker();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var id = _jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, Key, Key);
            await WaitUntilAsync(() => _store.Opened);

            _maintenance.ActiveJobs.Should().Be(1, "a restore must wait for a download in progress");
            // The restore begins while the file is still coming down: the counted
            // download is let through the guard to finish.
            _maintenance.Enter("Restoring backup test.dump");
            _store.Gate.SetResult();
            var job = await WaitForTerminalAsync(id);

            job.Status.Should().Be(OffsiteRestoreJobStatus.Completed, job.Error);
            _maintenance.ActiveJobs.Should().Be(0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_backups_row_waits_for_a_backup_or_restore_holding_the_lock()
    {
        await ConfigureOffsiteAsync();
        var held = await BackupCoordination.AcquireAsync(_db.ConnectionString, TestContext.Current.CancellationToken);
        Task<int> download;
        await using (var ctx = _db.NewContext())
        {
            download = NewOffsite(ctx).DownloadAsync(Key, progress: null, recoverFromAnotherInstallation: false,
                TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => File.Exists(Path.Combine(_options.Directory, Key)));
            await Task.Delay(300, TestContext.Current.CancellationToken);

            download.IsCompleted.Should().BeFalse("the row must not be written while the lock is held");
            await using (var check = _db.NewContext())
            {
                (await check.Backups.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
            }

            await held.DisposeAsync();
            var id = await download;

            await using var after = _db.NewContext();
            (await after.Backups.AsNoTracking().AnyAsync(b => b.Id == id, TestContext.Current.CancellationToken))
                .Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_counted_download_lets_a_restore_that_began_meanwhile_go_ahead_and_lands_its_row_after_it()
    {
        await ConfigureOffsiteAsync();
        _store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = NewWorker();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var id = _jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, Key, Key);
            await WaitUntilAsync(() => _store.Opened);
            _maintenance.ActiveJobs.Should().Be(1);

            // A restore begins as the restore does: the backup lock first, then
            // maintenance mode, then the wait for running jobs.
            var restoreLock = await BackupCoordination.AcquireAsync(_db.ConnectionString, TestContext.Current.CancellationToken);
            _maintenance.Enter("Restoring backup test.dump");
            _store.Gate.SetResult();

            (await _maintenance.WaitForActiveJobsAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken))
                .Should().BeTrue("once the file is down, the download must not hold the restore up");
            _jobs.Get(id)!.Status.Should().Be(OffsiteRestoreJobStatus.Running, "its row waits for the restore");
            await using (var check = _db.NewContext())
            {
                (await check.Backups.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
            }

            // The restore finishes: the site reopens and the lock is released.
            _maintenance.Exit();
            await restoreLock.DisposeAsync();
            var job = await WaitForTerminalAsync(id);

            job.Status.Should().Be(OffsiteRestoreJobStatus.Completed, job.Error);
            _maintenance.ActiveJobs.Should().Be(0, "the count is released once, not twice");
            await using var after = _db.NewContext();
            (await after.Backups.AsNoTracking().AnyAsync(b => b.Id == job.BackupId, TestContext.Current.CancellationToken))
                .Should().BeTrue();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_per_tenant_download_whose_row_cannot_be_written_leaves_no_file_behind()
    {
        await ConfigureOffsiteAsync();
        PerTenantBackup snapshot;
        await using (var ctx = _db.NewContext())
        {
            snapshot = await NewPerTenant(ctx).CreateAsync(TestDb.DefaultOrgId, BackupKind.AdHoc, TestContext.Current.CancellationToken);
        }
        var snapshotDir = Path.Combine(_options.Directory, "tenants", TestDb.DefaultOrgSlug);
        _store.Body = await File.ReadAllBytesAsync(Path.Combine(snapshotDir, snapshot.FileName), TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(snapshotDir, snapshot.FileName));
        var fileName = $"{TestDb.DefaultOrgSlug}-pulled{PerTenantBackupService.FileSuffix}";
        var key = $"{OffsiteBackupService.PerTenantKeyPrefix}{TestDb.DefaultOrgSlug}/{fileName}";

        // The lock is held and the wait for it is cancelled, so the insert never happens.
        await using var held = await BackupCoordination.AcquireAsync(_db.ConnectionString, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await using var ctx2 = _db.NewContext();
        var download = NewOffsite(ctx2).DownloadPerTenantAsync(key, progress: null, cts.Token);
        await WaitUntilAsync(() => File.Exists(Path.Combine(snapshotDir, fileName)));
        await cts.CancelAsync();

        var act = () => download;
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(snapshotDir, fileName)).Should().BeFalse("a file with no row would block a retry");
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<OffsiteRestoreJob> RunJobAsync()
    {
        var worker = NewWorker();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var id = _jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, Key, Key);
            return await WaitForTerminalAsync(id);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private OffsiteRestoreWorker NewWorker()
    {
        var services = new ServiceCollection();
        // As in production: a fresh scope per job, its context behind the guard,
        // and no request or SiteAdmin behind it.
        services.AddScoped(_ => _db.NewContext(new MaintenanceDatabaseGuard(
            _maintenance, new AmbientOrganizationContext(), new TestDb.FixedHttpContextAccessor(null))));
        services.AddScoped(sp => NewOffsite(sp.GetRequiredService<AppDbContext>()));
        return new OffsiteRestoreWorker(
            _jobs, services.BuildServiceProvider(), NullLogger<OffsiteRestoreWorker>.Instance,
            new WorkerHeartbeatRegistry(), _maintenance)
        {
            MaintenancePollInterval = TimeSpan.FromMilliseconds(20),
        };
    }

    private async Task<OffsiteRestoreJob> WaitForTerminalAsync(Guid id)
    {
        await WaitUntilAsync(() => _jobs.Get(id)!.Status is OffsiteRestoreJobStatus.Completed or OffsiteRestoreJobStatus.Failed);
        return _jobs.Get(id)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }

    private async Task ConfigureOffsiteAsync()
    {
        await using var ctx = _db.NewContext();
        await NewSystemSettings(ctx).SaveOffsiteAsync(new OffsiteSettingsInput(
            Enabled: true, Provider: null, Endpoint: null, Region: "eu-west-1", Bucket: "cronus-backups",
            Prefix: null, AccessKey: "access", ClearAccessKey: false, SecretKey: "secret", ClearSecretKey: false,
            ForcePathStyle: false, RetentionDays: 7));
    }

    private SystemSettingsService NewSystemSettings(AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString,
        })
        .Build();

    private OffsiteBackupService NewOffsite(AppDbContext ctx)
    {
        var backups = new BackupService(
            ctx, _db.OrgContext, _maintenance, Config(),
            NullLogger<BackupService>.Instance, _clock, _options, systemSettings: null, deployment: _us);
        return new OffsiteBackupService(
            ctx, NewSystemSettings(ctx), backups, NewPerTenant(ctx),
            new Factory(_store), NullLogger<OffsiteBackupService>.Instance, _clock, _us);
    }

    private PerTenantBackupService NewPerTenant(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), Config(),
            NullLogger<PerTenantBackupService>.Instance, _clock, _options, _us);

    private sealed class Factory(OneObjectProvider store) : IOffsiteStorageProviderFactory
    {
        public IOffsiteStorageProvider Create(ResolvedOffsiteSettings settings) => store;
    }

    /// <summary>A bucket holding one dump, which can be held part-way through its download.</summary>
    private sealed class OneObjectProvider : IOffsiteStorageProvider
    {
        public TaskCompletionSource? Gate { get; set; }

        public volatile bool Opened;

        public byte[] Body { get; set; } = [1, 2, 3];

        public async Task<OffsiteDownload> OpenReadAsync(string key, CancellationToken ct)
        {
            Opened = true;
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            return new OffsiteDownload(new MemoryStream(Body), new Dictionary<string, string>(), Body.Length);
        }

        public Task<OffsiteTestResult> TestConnectionAsync(CancellationToken ct) =>
            Task.FromResult(new OffsiteTestResult(true, "ok"));

        public Task UploadAsync(string key, Stream content, string contentType,
            IReadOnlyDictionary<string, string> metadata, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<OffsiteStorageObject>> ListAsync(string prefix, bool topLevelOnly, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OffsiteStorageObject>>([]);

        public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct) => Task.CompletedTask;
    }
}
