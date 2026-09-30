using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Shared plumbing for the Stage 4b upgrade-action tests (issue #657): a seeded customer
/// with a connected Business Central, a team carrying the environment-updates flag, and a
/// fake admin client standing in for Microsoft.
///
/// <para>The worker resolves its dependencies from a real
/// <see cref="IServiceProvider"/> and runs under an
/// <see cref="AmbientOrganizationScope"/>, so the provider built here registers an
/// organisation context that reads that ambient identity — the same fallback
/// <c>HttpOrganizationContext</c> performs in the app. Faking it any other way would test
/// a worker that doesn't exist.</para>
/// </summary>
internal sealed class UpgradeActionTestFixture : IDisposable
{
    public const int OwnerUserId = 9700;
    public const int FlagUserId = 9701;
    public const int PlainTeamUserId = 9702;
    public const int OutsiderUserId = 9703;

    public TestDb Db { get; } = new();
    public FakeUpdatesAdminClient Admin { get; } = new();
    /// <summary>Stands in for the App Management API: records an install, throws on everything else.</summary>
    public FakeUploadAppClient Apps { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero));

    /// <summary>How long the worker's sends wait for an install; a test sets it to zero to run the wait out.</summary>
    public TimeSpan UploadPollTimeout { get; set; } = ProjectConnectionService.DefaultUploadPollTimeout;

    private ServiceProvider? _provider;

    public UpgradeActionTestFixture()
    {
        using var ctx = Db.NewContext();
        ctx.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com", UserRole.Editor),
            NewUser(FlagUserId, "upgrade@example.com", UserRole.User),
            NewUser(PlainTeamUserId, "colleague@example.com", UserRole.User),
            NewUser(OutsiderUserId, "outsider@example.com", UserRole.User));
        ctx.SaveChanges();
        Db.OrgContext.CurrentUserId = FlagUserId;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        Db.Dispose();
    }

    private static User NewUser(int id, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email == "upgrade@example.com" ? "Anna Jensen" : email,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    public void ActAs(int? userId)
    {
        Db.OrgContext.CurrentUserId = userId;
        Db.OrgContext.IsSiteAdmin = false;
    }

    // ── The services under test ─────────────────────────────────────────

    public UpgradeActionService Svc(AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, Db.OrgContext);
        return new UpgradeActionService(ctx, Db.OrgContext, access, Connections(ctx, access), Clock,
            NullLogger<UpgradeActionService>.Instance);
    }

    /// <summary>The planned-upgrade service (#984), over the real fleet read.</summary>
    public EnvironmentUpgradeService Upgrades(AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, Db.OrgContext);
        var fleet = new UpgradeFleetService(ctx, Db.OrgContext, access, new EnvironmentRefreshQueue(),
            NullLogger<UpgradeFleetService>.Instance);
        return new EnvironmentUpgradeService(ctx, Db.OrgContext, access, fleet, Clock,
            NullLogger<EnvironmentUpgradeService>.Instance);
    }

    /// <summary>The connection service on its own, for the writes that book an upload.</summary>
    public ProjectConnectionService Connections(AppDbContext ctx) => Connections(ctx, new ProjectAccess(ctx, Db.OrgContext));

    private ProjectConnectionService Connections(AppDbContext ctx, ProjectAccess access) => new(
        ctx, Db.OrgContext, access, TokenOk(), Admin, Apps,
        Db.DataProtectionProvider,
        new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System), Clock,
        NullLogger<ProjectConnectionService>.Instance)
    {
        // A poll every five seconds is right for Business Central and wrong for a test.
        UploadPollDelay = TimeSpan.Zero,
    };

    /// <summary>
    /// A service provider shaped like the app's, for the worker: scoped context reading
    /// the ambient identity, the fake admin client, and a clock the test drives.
    /// </summary>
    public IServiceProvider Provider()
    {
        if (_provider is not null) return _provider;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IOrganizationContext, AmbientOnlyOrganizationContext>();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(Db.ConnectionString), ServiceLifetime.Scoped);
        services.AddSingleton(Db.DataProtectionProvider);
        services.AddSingleton<IBcAdminClient>(Admin);
        services.AddSingleton<IBcAppManagementClient>(Apps);
        services.AddSingleton(TokenOk());
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped(sp => new ProjectConnectionService(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IOrganizationContext>(),
            sp.GetRequiredService<ProjectAccess>(), sp.GetRequiredService<BcTokenService>(),
            sp.GetRequiredService<IBcAdminClient>(), sp.GetRequiredService<IBcAppManagementClient>(),
            sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            sp.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>(), sp.GetRequiredService<TimeProvider>(),
            NullLogger<ProjectConnectionService>.Instance)
        {
            UploadPollDelay = TimeSpan.Zero,
            UploadPollTimeout = UploadPollTimeout,
        });
        services.AddScoped<UpgradeActionService>();
        return _provider = services.BuildServiceProvider();
    }

    public UpgradeActionWorker Worker(WorkerHeartbeatRegistry? heartbeats = null) => new(
        Provider(), Clock, NullLogger<UpgradeActionWorker>.Instance, heartbeats ?? new WorkerHeartbeatRegistry());

    /// <summary>A token service whose every request succeeds, for a page test that drives the real services.</summary>
    public BcTokenService TokenService() => TokenOk();

    private BcTokenService TokenOk() =>
        new(new StubFactory(new StubHandler(HttpStatusCode.OK, "{\"access_token\":\"tok\",\"expires_in\":3600}")),
            NullLogger<BcTokenService>.Instance);

    // ── Seeding ─────────────────────────────────────────────────────────

    /// <summary>A customer with credentials, one Production environment, and an upgrade team assigned.</summary>
    public async Task<(int ProjectId, int EnvironmentId)> SeedCustomerAsync(
        string name = "CRONUS Denmark", string timeZone = "Europe/Copenhagen")
    {
        int projectId;
        await using (var ctx = Db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = name,
                CreatedByUserId = OwnerUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            projectId = project.Id;
        }

        // Credentials go in through the service so the secret is protected the way the
        // app protects it — the worker decrypts it for real.
        var owner = Db.OrgContext.CurrentUserId;
        Db.OrgContext.CurrentUserId = OwnerUserId;
        await using (var ctx = Db.NewContext())
        {
            var access = new ProjectAccess(ctx, Db.OrgContext);
            await Connections(ctx, access).SaveConnectionAsync(projectId, new BcConnectionInput(
                Guid.NewGuid(), "client-abc", "s3cr3t", DateTime.UtcNow.AddYears(1), timeZone));
        }
        Db.OrgContext.CurrentUserId = owner;

        int environmentId;
        await using (var ctx = Db.NewContext())
        {
            var env = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Name = "Production",
                Type = "Production",
                ApplicationFamily = "BusinessCentral",
                Status = "Active",
                Version = "27.5.12345.0",
                FetchedAt = DateTime.UtcNow,
            };
            ctx.OeProjectEnvironments.Add(env);
            await ctx.SaveChangesAsync();
            environmentId = env.Id;
        }

        await SeedUpdateTeamAsync(projectId);
        return (projectId, environmentId);
    }

    /// <summary>
    /// The flag holder and a plain colleague on one team assigned to the project — the
    /// only shape that grants the update-ops axis.
    /// </summary>
    public async Task SeedUpdateTeamAsync(int projectId)
    {
        await using var ctx = Db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = $"Upgrades {projectId}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();

        ctx.TeamMembers.Add(new TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = FlagUserId,
            ManagesUpdates = true, CreatedAt = DateTime.UtcNow,
        });
        ctx.TeamMembers.Add(new TeamMember
        {
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = PlainTeamUserId,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    public static readonly DateTimeOffset ScheduledDate = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset LatestDate = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    public static BcEnvironmentUpdate Update(
        DateTimeOffset? selectedDateTime, DateTimeOffset? latestSelectable,
        bool selected = true, bool ignoresWindow = false) =>
        new("27.6", true, selected, "scheduled", "GA", selectedDateTime, latestSelectable, ignoresWindow, "Active", null, null);

    /// <summary>Reads one action row straight from the database, bypassing the service.</summary>
    public async Task<OeEnvironmentUpgradeAction> ReadActionAsync(int actionId)
    {
        await using var ctx = Db.NewContext();
        return await ctx.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.Id == actionId);
    }

    // ── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Only the surface the update-date writes touch. Everything else throws, so a test
    /// that quietly starts using another call fails loudly rather than passing on a stub.
    /// </summary>
    public sealed class FakeUpdatesAdminClient : IBcAdminClient
    {
        /// <summary>What the environment's updates read returns; empty means "nothing waiting".</summary>
        public Func<IReadOnlyList<BcEnvironmentUpdate>> OnUpdates =
            () => new[] { Update(ScheduledDate, LatestDate) };

        /// <summary>How many date writes actually reached Business Central.</summary>
        public int Writes;
        public DateTimeOffset? SelectedDateTime;
        public bool? SelectedIgnoreUpdateWindow;
        /// <summary>The version the last write selected.</summary>
        public string? SelectedTargetVersion;

        public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(
            string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnUpdates());

        public Task SelectTargetVersionAsync(
            string accessToken, string? applicationFamily, string environmentName, string targetVersion,
            string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null,
            CancellationToken ct = default)
        {
            Writes++;
            SelectedTargetVersion = targetVersion;
            SelectedDateTime = selectedDateTime;
            SelectedIgnoreUpdateWindow = ignoreUpdateWindow;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcEnvironmentCopy> CopyEnvironmentAsync(string accessToken, string? applicationFamily, string sourceEnvironmentName, string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task CancelSessionAsync(string accessToken, string? applicationFamily, string environmentName, int sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    public sealed class FakeUploadAppClient : IBcAppManagementClient
    {
        /// <summary>The last install, so a test can pin the file, the bytes and the schedule.</summary>
        public (string FileName, byte[] Bytes, string Schedule)? Installed;
        /// <summary>Every install in the order it was sent.</summary>
        public List<string> InstalledFiles { get; } = new();
        public BcApiException? InstallThrows;
        /// <summary>What a poll of an install reports, by file name; succeeded straight away unless a test says otherwise.</summary>
        public Func<string, BcAppOperationStatus> OnOperationStatus = _ => BcAppOperationStatus.Succeeded;
        /// <summary>How many polls in a row answer with an API error before the status above is returned.</summary>
        public int PollErrorsBeforeAnswer;
        public int Polls;
        private readonly Dictionary<Guid, string> _operations = new();

        /// <summary>
        /// An operation Business Central is already running for <paramref name="name"/>, as
        /// one accepted before a restart would be: a poll answers for it like any other.
        /// Returns the ids a booking would have stamped.
        /// </summary>
        public (Guid AppId, Guid OperationId) Accepted(string name)
        {
            var operationId = Guid.NewGuid();
            _operations[operationId] = name;
            return (Guid.NewGuid(), operationId);
        }

        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        {
            if (InstallThrows is not null) throw InstallThrows;
            Installed = (fileName, appBytes, deploymentSchedule);
            InstalledFiles.Add(fileName);
            var operationId = Guid.NewGuid();
            _operations[operationId] = fileName;
            return Task.FromResult(new BcAppOperation(
                operationId, Guid.NewGuid(), "install", BcAppOperationStatus.Running, "running",
                string.Empty, "1.0.0.0", deploymentSchedule, string.Empty, string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, null));
        }

        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
        {
            Polls++;
            if (PollErrorsBeforeAnswer > 0)
            {
                PollErrorsBeforeAnswer--;
                throw new BcApiException(System.Net.HttpStatusCode.TooManyRequests, "Too many requests.");
            }
            var status = OnOperationStatus(_operations[operationId]);
            return Task.FromResult<BcAppOperation?>(new BcAppOperation(
                operationId, appId, "install", status, status.ToString().ToLowerInvariant(),
                string.Empty, "1.0.0.0", null,
                status == BcAppOperationStatus.Failed ? "The app needs Continia Core 28.0.0.0, which isn't installed." : string.Empty,
                status == BcAppOperationStatus.Failed ? "MissingDependency" : string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        /// <summary>The AppSource updates Business Central has waiting; none unless a test says otherwise.</summary>
        public Func<IReadOnlyList<BcAvailableAppUpdate>> OnAvailable = Array.Empty<BcAvailableAppUpdate>;
        /// <summary>Every AppSource update sent: the app, the version, whether the update window was used and whether prerequisites came along.</summary>
        public List<(Guid AppId, string Version, bool InWindow, bool WithDependencies)> Updated { get; } = new();

        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnAvailable());
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        {
            Updated.Add((appId, targetVersion, useEnvironmentUpdateWindow, installOrUpdateNeededDependencies));
            // Into the one send log with the uploads, so a test can pin the order across both.
            var name = $"update {appId}";
            InstalledFiles.Add(name);
            var operationId = Guid.NewGuid();
            _operations[operationId] = name;
            return Task.FromResult(new BcAppOperation(
                operationId, appId, "update", BcAppOperationStatus.Running, "running",
                string.Empty, targetVersion, null, string.Empty, string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, null));
        }
    }

    private sealed class UnusedAppManagementClient : IBcAppManagementClient
    {
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// An organisation context with no request behind it: it answers from
    /// <see cref="AmbientOrganizationScope.Current"/> alone, which is exactly what the
    /// worker relies on in production.
    /// </summary>
    private sealed class AmbientOnlyOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => AmbientOrganizationScope.Current?.OrganizationId;
        public int? CurrentUserId => AmbientOrganizationScope.Current?.UserId;
        public bool IsSiteAdmin => AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
        public bool IsSystemOrganization => AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
        public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
