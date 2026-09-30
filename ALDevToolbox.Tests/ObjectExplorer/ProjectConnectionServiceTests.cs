using System.Net;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Contract for <see cref="ProjectConnectionService"/> (the BC SaaS delivery
/// connection): the client secret is encrypted on write and never returned;
/// validation rejects missing credentials; Test connection persists the fetched
/// environments and stamps "verified"; a missing GDAP and rejected credentials are
/// classified distinctly; refresh is a stable upsert that preserves a row's id and
/// per-environment settings; and the owner-or-admin gate guards every mutation. The BC HTTP
/// surfaces are faked (the same seam reason <c>IProcessRunner</c> exists), and the
/// OAuth token call runs against a stub <see cref="IHttpClientFactory"/>.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ProjectConnectionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9400;

    /// <summary>
    /// One clock and one panel cache per test. The cache is a singleton in the app, so a
    /// per-test instance is what keeps one test's cached panel out of the next test.
    /// </summary>
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);
    private readonly BcPanelCache _panelCache;

    public ProjectConnectionServiceTests()
    {
        _panelCache = new BcPanelCache(_clock);

        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    // ── Test doubles ──────────────────────────────────────────────────────

    private sealed class FakeAdminClient : IBcAdminClient
    {
        /// <summary>
        /// Every read of the tenant, in order, as "Method:environment" - so a test can pin
        /// exactly how many requests an operation cost.
        /// </summary>
        public List<string> Reads { get; } = new();

        public Func<IReadOnlyList<BcEnvironment>> OnList = () => Array.Empty<BcEnvironment>();
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
        {
            Reads.Add("ListEnvironments");
            return Task.FromResult(OnList());
        }

        /// <summary>
        /// The by-name read. Only the Upgrades page's watch reaches it from this service
        /// (the delivery gate uses its own client); unset, it refuses, so nothing else
        /// starts using it unnoticed.
        /// </summary>
        public Func<string, BcEnvironment?>? OnGetEnvironment;
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Reads.Add("GetEnvironment:" + environmentName);
            return OnGetEnvironment is { } read ? Task.FromResult(read(environmentName)) : throw new NotSupportedException();
        }

        /// <summary>Microsoft's update window per environment name. Throwing here stands in for a per-environment API failure.</summary>
        public Func<string, BcUpdateSettings?> OnUpdateSettings = _ => null;
        public List<string> UpdateSettingsRequested { get; } = new();

        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Reads.Add("GetUpdateSettings:" + environmentName);
            UpdateSettingsRequested.Add(environmentName);
            return Task.FromResult(OnUpdateSettings(environmentName));
        }

        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();

        /// <summary>What the last settings write asked for, so a test can pin the payload.</summary>
        public string? Cadence;
        public bool? M365;
        public string? SelectedVersion;
        public string? SelectedVersionType;
        /// <summary>The date and window flag of the last version write, so a test can pin what the PATCH carried.</summary>
        public DateTimeOffset? SelectedDateTime;
        public bool? SelectedIgnoreUpdateWindow;
        /// <summary>How many version writes reached the client, so a refusal can be shown to have sent nothing.</summary>
        public int SelectWrites;
        public BcApiException? WriteThrows;

        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BcTimeZone>>(new[]
            {
                new BcTimeZone("Romance Standard Time", "(UTC+01:00) Brussels, Copenhagen, Madrid, Paris", "+01:00"),
            });

        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            Cadence = cadence;
            return Task.CompletedTask;
        }

        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(M365);

        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            M365 = enabled;
            return Task.CompletedTask;
        }

        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
        {
            if (WriteThrows is not null) throw WriteThrows;
            SelectedVersion = targetVersion;
            SelectedVersionType = targetVersionType;
            SelectedDateTime = selectedDateTime;
            SelectedIgnoreUpdateWindow = ignoreUpdateWindow;
            SelectWrites++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// What was asked to be recovered, and what Business Central answered. The hook is
        /// a Func so a test can make one environment refuse while another succeeds.
        /// </summary>
        public List<string> Recovered { get; } = new();
        public Func<string, BcApiException?> OnRecover = _ => null;

        public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            if (OnRecover(environmentName) is { } refusal) throw refusal;
            Recovered.Add(environmentName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Every copy asked for, as (source, new name, type), so a test can pin exactly
        /// what reached the tenant - and show that a refusal sent nothing at all.
        /// </summary>
        public List<(string Source, string NewName, string Type)> Copies { get; } = new();
        public BcApiException? CopyThrows;

        public Task<BcEnvironmentCopy> CopyEnvironmentAsync(
            string accessToken, string? applicationFamily, string sourceEnvironmentName,
            string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default)
        {
            if (CopyThrows is not null) throw CopyThrows;
            Copies.Add((sourceEnvironmentName, newEnvironmentName, newEnvironmentType));
            return Task.FromResult(new BcEnvironmentCopy("op-1f8f", "scheduled"));
        }

        /// <summary>
        /// Who is signed in, per environment name, and every session the service asked to
        /// end - so a test can show that a refusal ended nothing at all.
        /// </summary>
        public Func<string, IReadOnlyList<BcSession>> OnSessions { get; set; } = _ => Array.Empty<BcSession>();
        public List<(string Environment, int SessionId)> Cancelled { get; } = new();
        public BcApiException? SessionsThrows;
        public BcApiException? CancelSessionThrows;

        public Task<IReadOnlyList<BcSession>> ListSessionsAsync(
            string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            if (SessionsThrows is not null) throw SessionsThrows;
            return Task.FromResult(OnSessions(environmentName));
        }

        public Task CancelSessionAsync(
            string accessToken, string? applicationFamily, string environmentName, int sessionId,
            CancellationToken ct = default)
        {
            if (CancelSessionThrows is not null) throw CancelSessionThrows;
            Cancelled.Add((environmentName, sessionId));
            return Task.CompletedTask;
        }

        /// <summary>Platform updates per environment name; throwing stands in for a denied read.</summary>
        public Func<string, IReadOnlyList<BcEnvironmentUpdate>> OnEnvironmentUpdates = _ => Array.Empty<BcEnvironmentUpdate>();

        public Func<string, IReadOnlyList<BcEnvironmentOperation>> OnOperations { get; set; } = _ => Array.Empty<BcEnvironmentOperation>();
        public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnOperations(environmentName));
        public Func<BcTenantStorage> OnStorage { get; set; } = () => new BcTenantStorage(new Dictionary<string, long>(), null);
        public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
        {
            Reads.Add("GetTenantStorage");
            return Task.FromResult(OnStorage());
        }
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Reads.Add("ListEnvironmentUpdates:" + environmentName);
            return Task.FromResult(OnEnvironmentUpdates(environmentName));
        }
    }

    /// <summary>
    /// The App Management surface as the environment panel uses it. Each list has its own
    /// hook so a test can deny one section and prove the other three still render.
    /// </summary>
    private sealed class FakeAppManagementClient : IBcAppManagementClient
    {
        public Func<IReadOnlyList<BcInstalledApp>> OnInstalled = Array.Empty<BcInstalledApp>;
        public Func<IReadOnlyList<BcAvailableAppUpdate>> OnAvailable = Array.Empty<BcAvailableAppUpdate>;
        public Func<IReadOnlyList<BcScheduledPteOperation>> OnScheduled = Array.Empty<BcScheduledPteOperation>;

        /// <summary>What a cancel was asked to remove, so a test can pin the three identifying values.</summary>
        public (Guid AppId, string Version, string ScheduleKind)? Removed;
        public BcApiException? RemoveThrows;

        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnInstalled());
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnAvailable());
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult(OnScheduled());

        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
        {
            if (RemoveThrows is not null) throw RemoveThrows;
            Removed = (appId, targetVersion, scheduleKind);
            return Task.FromResult(new BcAppOperation(
                Guid.NewGuid(), appId, "install", BcAppOperationStatus.Canceled, "canceled",
                string.Empty, targetVersion, scheduleKind, string.Empty, string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow));
        }

        /// <summary>What an update was asked to do, so a test can pin the version and the timing.</summary>
        public (Guid AppId, string Version, bool InWindow)? Updated;
        public bool? UpdatedWithDependencies;

        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        {
            Updated = (appId, targetVersion, useEnvironmentUpdateWindow);
            UpdatedWithDependencies = installOrUpdateNeededDependencies;
            return Task.FromResult(new BcAppOperation(
                Guid.NewGuid(), appId, "update", BcAppOperationStatus.Scheduled, "scheduled",
                string.Empty, targetVersion, null, string.Empty, string.Empty, string.Empty,
                true, "app", DateTimeOffset.UtcNow, null, null));
        }

        public (string FileName, int Bytes, string Schedule, string SyncMode, bool WithDependencies)? Installed;
        public BcApiException? InstallThrows;

        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        {
            if (InstallThrows is not null) throw InstallThrows;
            Installed = (fileName, appBytes.Length, deploymentSchedule, syncMode, installOrUpdateNeededDependencies);
            return Task.FromResult(new BcAppOperation(
                Guid.NewGuid(), Guid.NewGuid(), "install", BcAppOperationStatus.Running, "running",
                string.Empty, "1.0.0.0", deploymentSchedule, string.Empty, string.Empty, string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, null));
        }
        /// <summary>What a poll of an install reports; succeeded straight away unless a test says otherwise.</summary>
        public Func<Guid, BcAppOperation?> OnOperation = id => new BcAppOperation(
            id, Guid.NewGuid(), "install", BcAppOperationStatus.Succeeded, "succeeded",
            string.Empty, "1.0.0.0", null, string.Empty, string.Empty, string.Empty,
            false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);

        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
            => Task.FromResult(OnOperation(operationId));
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

    /// <summary>A token service whose login round-trip returns a fixed 200 token.</summary>
    private BcTokenService TokenOk() =>
        new(new StubFactory(new StubHandler(HttpStatusCode.OK, "{\"access_token\":\"tok\",\"expires_in\":3600}")),
            NullLogger<BcTokenService>.Instance);

    /// <summary>A token service whose login round-trip is rejected (bad creds).</summary>
    private BcTokenService TokenRejected() =>
        new(new StubFactory(new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")),
            NullLogger<BcTokenService>.Instance);

    private ProjectConnectionService Svc(
        ALDevToolbox.Data.AppDbContext ctx,
        BcTokenService tokens,
        IBcAdminClient? admin = null,
        IBcAppManagementClient? apps = null)
        => new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), tokens,
            admin ?? new FakeAdminClient(), apps ?? new FakeAppManagementClient(),
            _db.DataProtectionProvider, _panelCache, _clock,
            NullLogger<ProjectConnectionService>.Instance);

    /// <summary>A clock the tests move by hand, so the panel cache's TTL is testable.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private async Task<int> SeedProjectAsync(string name = "CRONUS A/S")
    {
        await using var ctx = _db.NewContext();
        var p = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private BcConnectionInput ValidConnection(string secret = "s3cr3t") =>
        new(Guid.NewGuid(), "client-abc", secret, DateTime.UtcNow.AddYears(1), "Europe/Copenhagen");

    // ── Secret handling ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveConnection_encrypts_secret_and_never_returns_it()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection("plaintext-secret"));
        }

        // The status view exposes presence, never the secret.
        await using (var ctx = _db.NewContext())
        {
            var status = await Svc(ctx, TokenOk()).GetConnectionAsync(id);
            status!.HasSecret.Should().BeTrue();
            status.IsConfigured.Should().BeTrue();
        }

        // The stored column is ciphertext that round-trips only through the protector.
        await using (var verify = _db.NewContext())
        {
            var stored = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => p.BcClientSecretEncrypted).SingleAsync();
            stored.Should().NotBeNullOrEmpty();
            stored.Should().NotBe("plaintext-secret", "the secret is stored encrypted, never as plaintext");
            _db.DataProtectionProvider
                .CreateProtector(ProjectConnectionService.SecretProtectionPurpose)
                .Unprotect(stored!).Should().Be("plaintext-secret");
        }
    }

    [Fact]
    public async Task SaveConnection_keeps_existing_secret_on_blank()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection("keep-me"));

        string? before;
        await using (var ctx = _db.NewContext())
            before = await ctx.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => p.BcClientSecretEncrypted).SingleAsync();

        // Re-save with a blank secret but a changed timezone.
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), "client-abc", ClientSecret: "", SecretExpiresAt: null, TimeZone: "UTC"));

        await using (var verify = _db.NewContext())
        {
            var after = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
                .Select(p => new { p.BcClientSecretEncrypted, p.BcTimeZone }).SingleAsync();
            after.BcClientSecretEncrypted.Should().Be(before, "a blank secret leaves the stored one untouched");
            after.BcTimeZone.Should().Be("UTC", "other fields still update");
        }
    }

    [Fact]
    public async Task SaveConnection_rejects_missing_credentials()
    {
        var id = await SeedProjectAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx, TokenOk()).SaveConnectionAsync(id,
            new BcConnectionInput(TenantId: null, ClientId: "", ClientSecret: null, SecretExpiresAt: null, TimeZone: null));

        var ex = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
        ex.Errors.Should().ContainKey("BcTenantId");
        ex.Errors.Should().ContainKey("BcClientId");
        ex.Errors.Should().ContainKey("BcClientSecret");
    }

    [Fact]
    public async Task SaveConnection_requires_expiry_when_setting_a_secret()
    {
        var id = await SeedProjectAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx, TokenOk()).SaveConnectionAsync(id,
            new BcConnectionInput(Guid.NewGuid(), "client-abc", "secret", SecretExpiresAt: null, TimeZone: null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("BcClientSecretExpiresAt");
    }

    // ── Environment listing ───────────────────────────────────────────────

    /// <summary>
    /// Production sorts above sandboxes regardless of name — a customer with several
    /// sandboxes would otherwise bury the environment that matters most under
    /// alphabetical order ("Dev", "Preview", "Test" all precede "Production").
    /// </summary>
    [Fact]
    public async Task ListEnvironments_puts_production_before_sandboxes()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("Dev", "Sandbox"),
                new BcEnvironment("Live", "Production"),
                new BcEnvironment("Test", "Sandbox"),
                new BcEnvironment("Backup", "Production"),
            },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        IReadOnlyList<ProjectEnvironmentRow> rows;
        await using (var ctx = _db.NewContext())
            rows = await Svc(ctx, TokenOk(), admin).ListEnvironmentsAsync(id);

        rows.Select(r => r.Name).Should().Equal("Backup", "Live", "Dev", "Test");
    }

    // ── Test connection ───────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_persists_environments_and_marks_verified()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("Sandbox", "Sandbox") },
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Result.Should().Be(BcConnectionResult.Success);
        result.EnvironmentCount.Should().Be(2);

        await using (var verify = _db.NewContext())
        {
            (await verify.OeProjectEnvironments.CountAsync(e => e.ProjectId == id)).Should().Be(2);
            (await verify.OeProjects.Where(p => p.Id == id).Select(p => p.BcConnectionVerifiedAt).SingleAsync())
                .Should().NotBeNull("a successful test stamps the verified time");
        }
    }

    /// <summary>
    /// 401 and 403 from the Admin Center API are different failures with different
    /// fixes — the app missing from BC's authorized-apps list vs. the app lacking
    /// permission — and collapsing them into one "GDAP is missing" message sent a
    /// real user hunting a GDAP relationship that their own-tenant setup never needed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BcConnectionResult.AppNotAuthorized)]
    [InlineData(HttpStatusCode.Forbidden, BcConnectionResult.AccessDenied)]
    public async Task TestConnection_distinguishes_unauthorized_from_forbidden(
        HttpStatusCode status, BcConnectionResult expected)
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => throw new BcApiException(status, "denied"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Result.Should().Be(expected);
        result.IsSuccess.Should().BeFalse();
        await using var verify = _db.NewContext();
        (await verify.OeProjects.Where(p => p.Id == id).Select(p => p.BcConnectionVerifiedAt).SingleAsync())
            .Should().BeNull("a denied environments call doesn't count as verified");
    }

    /// <summary>
    /// The 401 message has to name the fix, because the thing to change isn't in the
    /// Entra portal the user was just looking at — it's BC's own authorized-apps list.
    /// </summary>
    [Fact]
    public async Task TestConnection_401_message_points_at_the_authorized_apps_list()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => throw new BcApiException(HttpStatusCode.Unauthorized, "denied"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).TestConnectionAsync(id);

        result.Message.Should().Contain("Authorized Microsoft Entra apps");
        result.Message.Should().NotContain("GDAP", "a 401 is not evidence of a missing GDAP relationship");
    }

    [Fact]
    public async Task TestConnection_reports_auth_failure_when_credentials_rejected()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenRejected()).TestConnectionAsync(id);

        result.Result.Should().Be(BcConnectionResult.AuthFailed);
    }

    [Fact]
    public async Task Refresh_is_a_stable_upsert_preserving_id_and_settings()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        // Pre-seed an environment carrying a setting of its own, and one that will vanish.
        int prodId;
        await using (var seed = _db.NewContext())
        {
            var prod = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "Production",
                Type = "Production", FetchedAt = DateTime.UtcNow.AddDays(-1),
                UpdateWindowStart = new TimeOnly(22, 0), UpdateWindowEnd = new TimeOnly(6, 0),
            };
            seed.OeProjectEnvironments.Add(prod);
            seed.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "OldSandbox",
                Type = "Sandbox", FetchedAt = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
            prodId = prod.Id;
        }

        var admin = new FakeAdminClient
        {
            // Production still present (type unchanged), a brand-new Sandbox, OldSandbox gone.
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("NewSandbox", "Sandbox") },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == id).ToListAsync();

        var prodRow = rows.Single(e => e.Name == "Production");
        prodRow.Id.Should().Be(prodId, "the row identity is preserved across a refresh");
        prodRow.UpdateWindowStart.Should().Be(new TimeOnly(22, 0), "a setting made on the row survives a refresh");
        prodRow.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
        prodRow.MissingSince.Should().BeNull();

        rows.Should().Contain(e => e.Name == "NewSandbox" && e.MissingSince == null);
        rows.Single(e => e.Name == "OldSandbox").MissingSince
            .Should().NotBeNull("an environment the customer removed is flagged, not deleted");
    }

    // ── Default delivery windows for new environments (issue #962) ─────────

    private async Task SetDefaultWindowsAsync(DefaultDeliveryWindows windows)
    {
        await using var ctx = _db.NewContext();
        await _db.NewOrganizationAdminService(ctx).SetDefaultDeliveryWindowsAsync(windows);
    }

    private static readonly DefaultDeliveryWindows NightProductionEveningSandbox = new(
        new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(18, 0), new TimeOnly(20, 0));

    private async Task<List<OeProjectEnvironment>> EnvironmentsAsync(int projectId)
    {
        await using var verify = _db.NewContext();
        return await verify.OeProjectEnvironments.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync();
    }

    [Fact]
    public async Task A_first_seen_environment_takes_the_organisations_default_window_for_its_type()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SetDefaultWindowsAsync(NightProductionEveningSandbox);

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Live", "Production"), new BcEnvironment("Test", "Sandbox") },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        var rows = await EnvironmentsAsync(id);
        var live = rows.Single(e => e.Name == "Live");
        live.UpdateWindowStart.Should().Be(new TimeOnly(22, 0), "a Production environment takes the Production default");
        live.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
        var test = rows.Single(e => e.Name == "Test");
        test.UpdateWindowStart.Should().Be(new TimeOnly(18, 0), "a Sandbox takes the Sandbox default");
        test.UpdateWindowEnd.Should().Be(new TimeOnly(20, 0));
    }

    [Fact]
    public async Task A_second_refresh_leaves_a_window_changed_since_creation_alone()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SetDefaultWindowsAsync(NightProductionEveningSandbox);
        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Live", "Production") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);
        var envId = (await EnvironmentsAsync(id)).Single().Id;

        // Somebody clears it on the environment page, then the default itself changes.
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SetUpdateWindowAsync(id, envId, null, null);
        await SetDefaultWindowsAsync(new DefaultDeliveryWindows(new TimeOnly(1, 0), new TimeOnly(3, 0), null, null));
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        var row = (await EnvironmentsAsync(id)).Single();
        row.UpdateWindowStart.Should().BeNull("the default applies when a row is born, never to one that exists");
        row.UpdateWindowEnd.Should().BeNull();
    }

    [Fact]
    public async Task Setting_a_default_does_not_backfill_environments_already_known()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Live", "Production") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await SetDefaultWindowsAsync(NightProductionEveningSandbox);
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        var row = (await EnvironmentsAsync(id)).Single();
        row.UpdateWindowStart.Should().BeNull("an environment met before the default was set stays at any time");
        row.UpdateWindowEnd.Should().BeNull();
    }

    [Fact]
    public async Task With_no_default_a_new_environment_has_no_window()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        // Only Sandbox has a default: a Production environment must not borrow it.
        await SetDefaultWindowsAsync(new DefaultDeliveryWindows(null, null, new TimeOnly(18, 0), new TimeOnly(20, 0)));

        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Live", "Production") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        var row = (await EnvironmentsAsync(id)).Single();
        row.UpdateWindowStart.Should().BeNull();
        row.UpdateWindowEnd.Should().BeNull();
    }

    [Fact]
    public async Task A_rename_folded_onto_an_existing_row_does_not_take_the_default()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SeedLiveEnvironmentAsync(id, "JLE");
        await SetDefaultWindowsAsync(NightProductionEveningSandbox);

        var admin = new FakeAdminClient { OnList = () => new[] { SoftDeleted("JLE-260911110359") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        var row = (await EnvironmentsAsync(id)).Should().ContainSingle().Subject;
        row.UpdateWindowStart.Should().BeNull("the fold keeps the existing row, and an existing row is never touched");
    }

    // ── The soft-delete rename (issue #808) ───────────────────────────────

    /// <summary>Seeds one live environment and returns its row id.</summary>
    private async Task<int> SeedLiveEnvironmentAsync(int projectId, string name)
    {
        await using var seed = _db.NewContext();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = "Sandbox",
            Status = "Active",
            FetchedAt = DateTime.UtcNow.AddDays(-1),
        };
        seed.OeProjectEnvironments.Add(env);
        await seed.SaveChangesAsync();
        return env.Id;
    }

    private static BcEnvironment SoftDeleted(string name) =>
        new(name, "Sandbox")
        {
            Status = "SoftDeleted",
            SoftDeletedOn = new DateTime(2026, 9, 11, 11, 3, 59, DateTimeKind.Utc),
        };

    /// <summary>
    /// The pair a solution is left with when it met the renamed environment before the
    /// fold existed: the old name stuck on "no longer present", the stamped name beside
    /// it. The fold only ever ran on first sight, so nothing put these back together.
    /// </summary>
    [Fact]
    public async Task A_pair_already_split_by_a_soft_delete_is_merged_and_keeps_what_the_old_row_carried()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var oldId = await SeedLiveEnvironmentAsync(id, "JLE");
        var stampedId = await SeedLiveEnvironmentAsync(id, "JLE-260911110359");
        int releasePipelineId;
        await using (var seed = _db.NewContext())
        {
            var old = await seed.OeProjectEnvironments.SingleAsync(e => e.Id == oldId);
            old.MissingSince = DateTime.UtcNow.AddDays(-8);
            old.UpdateWindowStart = new TimeOnly(20, 0);
            old.UpdateWindowEnd = new TimeOnly(6, 0);
            var build = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "Build", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            seed.OePipelines.Add(build);
            await seed.SaveChangesAsync();
            var release = new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "CRONUS App -> JLE",
                BuildPipelineId = build.Id, ProjectEnvironmentId = oldId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleasePipelines.Add(release);
            await seed.SaveChangesAsync();
            releasePipelineId = release.Id;
        }

        var admin = new FakeAdminClient { OnList = () => new[] { SoftDeleted("JLE-260911110359") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = (await verify.OeProjectEnvironments.AsNoTracking().Where(e => e.ProjectId == id).ToListAsync())
            .Should().ContainSingle("one deletion is one environment, not two").Subject;
        row.Id.Should().Be(stampedId, "the row whose name the API answers to is the one that stays");
        row.SoftDeletedOn.Should().NotBeNull();
        row.UpdateWindowStart.Should().Be(new TimeOnly(20, 0), "the delivery window somebody agreed with the customer comes across");
        (await verify.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == releasePipelineId))
            .ProjectEnvironmentId.Should().Be(stampedId, "a release pipeline must never be left pointing at a deleted row");
    }

    [Fact]
    public async Task A_merged_pair_keeps_the_old_rows_place_on_a_planned_upgrade()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var oldId = await SeedLiveEnvironmentAsync(id, "JLE");
        var stampedId = await SeedLiveEnvironmentAsync(id, "JLE-260911110359");
        int upgradeId;
        await using (var seed = _db.NewContext())
        {
            var old = await seed.OeProjectEnvironments.SingleAsync(e => e.Id == oldId);
            old.MissingSince = DateTime.UtcNow.AddDays(-8);
            var upgrade = new OeEnvironmentUpgrade
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "28.5 in November 2026", TargetVersion = "28.5",
                CreatedBy = "Anna Jensen", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                Lines =
                [
                    new OeEnvironmentUpgradeLine
                    {
                        OrganizationId = TestDb.DefaultOrgId, EnvironmentId = oldId, ProjectId = id,
                        IsOpen = true, AddedAt = DateTime.UtcNow,
                    },
                ],
            };
            seed.OeEnvironmentUpgrades.Add(upgrade);
            await seed.SaveChangesAsync();
            upgradeId = upgrade.Id;
        }

        var admin = new FakeAdminClient { OnList = () => new[] { SoftDeleted("JLE-260911110359") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var line = await verify.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync(l => l.UpgradeId == upgradeId);
        line.EnvironmentId.Should().Be(stampedId, "deleting the old row must not cascade its place on the upgrade away");
    }

    [Fact]
    public async Task A_recreated_environment_beside_its_deleted_namesake_is_never_merged_away()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SeedLiveEnvironmentAsync(id, "JLE");
        await SeedLiveEnvironmentAsync(id, "JLE-260911110359");

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("JLE", "Sandbox") { Status = "Active" }, SoftDeleted("JLE-260911110359") },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        (await verify.OeProjectEnvironments.AsNoTracking().CountAsync(e => e.ProjectId == id)).Should().Be(2);
    }

    [Fact]
    public async Task A_soft_delete_renamed_by_business_central_folds_onto_the_row_it_came_from()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var jleId = await SeedLiveEnvironmentAsync(id, "JLE");

        // What the API does on a soft delete: the old name is gone and the environment
        // comes back with the deletion time appended.
        var admin = new FakeAdminClient { OnList = () => new[] { SoftDeleted("JLE-260911110359") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == id).ToListAsync();

        var row = rows.Should().ContainSingle("one deletion is one environment, not two").Subject;
        row.Id.Should().Be(jleId, "the row a release pipeline points at survives the rename");
        row.Name.Should().Be("JLE-260911110359", "the API name is what later admin-center calls address");
        row.SoftDeletedOn.Should().NotBeNull();
        row.MissingSince.Should().BeNull("the environment is deleted, not absent");
    }

    [Fact]
    public async Task A_renamed_soft_delete_does_not_fold_when_the_old_name_is_in_use_again()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SeedLiveEnvironmentAsync(id, "JLE");

        // The customer deleted JLE and created a fresh one under the same name.
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("JLE", "Sandbox") { Status = "Active" }, SoftDeleted("JLE-260911110359") },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == id).ToListAsync();

        rows.Should().HaveCount(2, "the live JLE and the deleted one are different environments");
        rows.Single(e => e.Name == "JLE").MissingSince.Should().BeNull();
        rows.Single(e => e.Name == "JLE-260911110359").SoftDeletedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task An_environment_named_like_a_stamp_but_alive_is_not_folded()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        await SeedLiveEnvironmentAsync(id, "JLE");

        // A customer is free to name an environment this way; only a soft delete folds.
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("JLE-260911110359", "Sandbox") { Status = "Active" } },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == id).ToListAsync();

        rows.Should().HaveCount(2);
        rows.Single(e => e.Name == "JLE").MissingSince.Should().NotBeNull("Business Central stopped reporting it");
        rows.Single(e => e.Name == "JLE-260911110359").SoftDeletedOn.Should().BeNull();
    }

    [Fact]
    public async Task The_hard_delete_after_a_fold_flags_the_row_as_no_longer_present()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var jleId = await SeedLiveEnvironmentAsync(id, "JLE");

        var soft = new FakeAdminClient { OnList = () => new[] { SoftDeleted("JLE-260911110359") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), soft).RefreshEnvironmentsAsync(id);

        // Days later the hard delete lands and the environment is gone from the API.
        var gone = new FakeAdminClient { OnList = () => Array.Empty<BcEnvironment>() };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), gone).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking()
            .SingleAsync(e => e.ProjectId == id);

        row.Id.Should().Be(jleId);
        row.MissingSince.Should().NotBeNull("a hard-deleted environment is flagged, not deleted");
    }

    [Theory]
    [InlineData("JLE-260911110359", "JLE")]
    [InlineData("JLE-TEST-260911110359", "JLE-TEST")]
    [InlineData("JLE", null)]
    [InlineData("JLE-26091111035", null)]      // eleven digits
    [InlineData("JLE-2609111103599", null)]    // thirteen digits
    [InlineData("JLE-26091111035x", null)]     // not all digits
    [InlineData("-260911110359", null)]        // nothing left of the stamp
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_deletion_stamp_is_only_stripped_from_a_name_that_carries_one(string? name, string? expected)
        => ProjectConnectionService.SoftDeleteStampedBaseName(name).Should().Be(expected);

    // ── The deletion mirror, and bringing an environment back ─────────────

    /// <summary>
    /// The three deletion fields are mirrored onto the row, because the pages that split
    /// deleted environments off from live ones read them without calling Business Central.
    /// </summary>
    [Fact]
    public async Task A_refresh_mirrors_when_the_environment_was_deleted_and_when_it_goes_for_good()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("Sandbox", "Sandbox")
                {
                    Status = "SoftDeleted",
                    SoftDeletedOn = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc),
                    HardDeletePendingOn = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc),
                    DeleteReason = "Deleted by the customer",
                },
            },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.SoftDeletedOn.Should().Be(new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));
        row.HardDeletePendingOn.Should().Be(new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc));
        row.DeleteReason.Should().Be("Deleted by the customer");
    }

    /// <summary>
    /// And cleared again once the environment is back. A stale deletion date would keep
    /// a live environment in the Deleted view and out of everything else - the mirror has
    /// to be able to say "no longer deleted", not only "deleted".
    /// </summary>
    [Fact]
    public async Task A_refresh_clears_the_deletion_dates_when_the_environment_is_live_again()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var deleted = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("Sandbox", "Sandbox")
                {
                    Status = "SoftDeleted",
                    SoftDeletedOn = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc),
                    HardDeletePendingOn = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc),
                    DeleteReason = "Deleted by the customer",
                },
            },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), deleted).RefreshEnvironmentsAsync(id);

        var recovered = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Sandbox", "Sandbox") { Status = "Active" } },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), recovered).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.Status.Should().Be("Active");
        row.SoftDeletedOn.Should().BeNull();
        row.HardDeletePendingOn.Should().BeNull();
        row.DeleteReason.Should().BeNull();
    }

    /// <summary>Seeds a project with one deleted environment and returns both ids.</summary>
    private async Task<(int ProjectId, int EnvironmentId)> SeedDeletedEnvironmentAsync()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("JLE-260911110359");
        await using var ctx = _db.NewContext();
        var row = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
        row.Status = "SoftDeleted";
        row.SoftDeletedOn = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        row.HardDeletePendingOn = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        await ctx.SaveChangesAsync();
        return (projectId, envId);
    }

    [Fact]
    public async Task Recovering_a_deleted_environment_asks_business_central_and_re_reads_the_list()
    {
        var (projectId, envId) = await SeedDeletedEnvironmentAsync();
        // The re-read afterwards is what moves the row on; Business Central reports the
        // environment as recovering by then.
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("JLE-260911110359", "Production") { Status = "Recovering" } },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RecoverEnvironmentAsync(projectId, envId);

        admin.Recovered.Should().ContainSingle().Which.Should().Be("JLE-260911110359");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.Status.Should().Be("Recovering", "the write is followed by a re-read, so the row moves on");
        row.SoftDeletedOn.Should().BeNull();
    }

    [Fact]
    public async Task Recovering_puts_a_line_in_the_environments_workbench_history()
    {
        var (projectId, envId) = await SeedDeletedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("JLE-260911110359", "Production") { Status = "Recovering" } },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RecoverEnvironmentAsync(projectId, envId);

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        entry.Kind.Should().Be(UpgradeActionKind.RecoverEnvironment);
        entry.Status.Should().Be(UpgradeActionStatus.Sent, "a record, never something for the worker to fire");
        entry.Outcome.Should().Contain("JLE-260911110359");
        entry.RequestedBy.Should().Contain("owner@example.com");
    }

    [Fact]
    public async Task An_environment_that_was_never_deleted_cannot_be_recovered()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).RecoverEnvironmentAsync(projectId, envId);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors["Environment"].Should().Contain("nothing to bring back");
        admin.Recovered.Should().BeEmpty("nothing was sent to the customer's tenant");
    }

    /// <summary>
    /// Business Central's two documented refusals reach the caller as sentences, not as
    /// error codes - and neither leaves a history line, since nothing happened.
    /// </summary>
    [Fact]
    public async Task A_recovery_business_central_refuses_comes_back_as_a_sentence()
    {
        var (projectId, envId) = await SeedDeletedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnRecover = _ => new BcApiException(
                HttpStatusCode.BadRequest,
                "Business Central is already bringing this environment back. Refresh in a few minutes to see it return."),
        };

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).RecoverEnvironmentAsync(projectId, envId);
            var thrown = await act.Should().ThrowAsync<PlanValidationException>();
            thrown.Which.Errors["Environment"].Should().Contain("already bringing this environment back");
        }

        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Someone_who_does_not_manage_the_solution_cannot_recover_its_environments()
    {
        var (projectId, envId) = await SeedDeletedEnvironmentAsync();
        await SeedUserAsync(9776, "onlooker@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9776;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).RecoverEnvironmentAsync(projectId, envId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        admin.Recovered.Should().BeEmpty();
    }

    // ── Copying an environment ────────────────────────────────────────────

    /// <summary>
    /// Business Central schedules the copy rather than making it there and then, so the
    /// write is followed by a re-read: the new environment appears as soon as Microsoft
    /// lists it, and on this read it usually has not yet - which must not be an error.
    /// </summary>
    [Fact]
    public async Task Copying_an_environment_sends_the_new_name_and_type_and_re_reads_the_list()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("Production", "Production") { Status = "Active" },
                new BcEnvironment("CRONUS-Test", "Sandbox") { Status = "Preparing" },
            },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
                projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        admin.Copies.Should().ContainSingle().Which
            .Should().Be(("Production", "CRONUS-Test", BcEnvironmentTypes.Sandbox));

        await using var verify = _db.NewContext();
        var names = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == projectId).Select(e => e.Name).ToListAsync();
        names.Should().Contain("CRONUS-Test", "the re-read afterwards is what brings the new one in");
    }

    /// <summary>The copy may not be listed for an hour, and that is not a failure of the write.</summary>
    [Fact]
    public async Task A_copy_business_central_has_not_listed_yet_is_not_an_error()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") { Status = "Active" } },
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        await act.Should().NotThrowAsync();
        admin.Copies.Should().ContainSingle();
    }

    [Fact]
    public async Task Copying_puts_a_line_on_the_source_environments_workbench_history()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") { Status = "Active" } },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
                projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
        entry.EnvironmentId.Should().Be(envId, "the line belongs to the environment that was copied");
        entry.Kind.Should().Be(UpgradeActionKind.CopyEnvironment);
        entry.Status.Should().Be(UpgradeActionStatus.Sent, "a record, never something for the worker to fire");
        entry.Outcome.Should().Contain("CRONUS-Test").And.Contain("sandbox");
        entry.RequestedBy.Should().Contain("owner@example.com");
    }

    /// <summary>
    /// Business Central's rules for an environment name, refused here rather than as a
    /// wire code minutes later. The service is the source of truth; the dialog mirrors it.
    /// </summary>
    [Theory]
    [InlineData("", "Enter a name")]
    [InlineData("   ", "Enter a name")]
    [InlineData("9Lives", "start with a letter")]
    [InlineData("-Test", "start with a letter")]
    [InlineData("CRONUS Test", "letters, numbers, dashes and underscores")]
    [InlineData("CRONUS.Test", "letters, numbers, dashes and underscores")]
    [InlineData("CRONUSCRONUSCRONUSCRONUSCRONUSCRONUS", "too long")]
    public async Task A_name_business_central_would_refuse_is_refused_before_anything_is_sent(
        string name, string expected)
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, name, BcEnvironmentTypes.Sandbox);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors["NewName"].Should().Contain(expected);
        admin.Copies.Should().BeEmpty("nothing was sent to the customer's tenant");
    }

    /// <summary>
    /// Our own mirror answers the commonest mistake before a round trip, and names the
    /// environment that is in the way. Case-insensitively: Business Central's names are.
    /// </summary>
    [Fact]
    public async Task A_name_this_solution_already_uses_is_refused_by_our_own_mirror()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, "production", BcEnvironmentTypes.Sandbox);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors["NewName"].Should().Contain("already has an environment called");
        admin.Copies.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_environment_cannot_be_copied()
    {
        var (projectId, envId) = await SeedDeletedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors["Environment"].Should().Contain("has been deleted");
        admin.Copies.Should().BeEmpty();
    }

    /// <summary>
    /// An environment part-way through an update is a moving target, and a copy of one is
    /// a copy of a moment nobody can name. The same reading the delivery gate makes.
    /// </summary>
    [Fact]
    public async Task An_environment_business_central_is_still_working_on_cannot_be_copied()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        await using (var seed = _db.NewContext())
        {
            var row = await seed.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
            row.Status = "Upgrading";
            await seed.SaveChangesAsync();
        }
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors["Environment"].Should().Contain("upgrading");
        admin.Copies.Should().BeEmpty();
    }

    /// <summary>A refusal from Business Central arrives as a sentence, and leaves no history line.</summary>
    [Fact]
    public async Task A_copy_business_central_refuses_comes_back_as_a_field_keyed_sentence()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        var admin = new FakeAdminClient
        {
            CopyThrows = new BcApiException(
                HttpStatusCode.BadRequest,
                "An environment with that name already exists in the customer's Business Central. Pick another name."),
        };

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
                projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);
            var thrown = await act.Should().ThrowAsync<PlanValidationException>();
            thrown.Which.Errors["Environment"].Should().Contain("already exists");
        }

        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId))
            .Should().BeFalse("nothing happened, so nothing is recorded");
    }

    [Fact]
    public async Task Someone_who_does_not_manage_the_solution_cannot_copy_its_environments()
    {
        var (projectId, envId) = await SeedEnvironmentAsync("Production");
        await SeedUserAsync(9777, "onlooker2@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9777;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).CopyEnvironmentAsync(
            projectId, envId, "CRONUS-Test", BcEnvironmentTypes.Sandbox);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        admin.Copies.Should().BeEmpty();
    }

    // ── Sessions ──────────────────────────────────────────────────────────

    private static BcSession Session(
        int id, string user, string clientType = "WebClient", TimeSpan? running = null,
        DateTimeOffset? since = null, string currentObject = "") => new(
        id, user, clientType, since,
        string.Empty, string.Empty, string.Empty, string.Empty,
        currentObject, currentObject.Length > 0 ? 82 : null, currentObject.Length > 0 ? "CodeUnit" : string.Empty,
        running);

    /// <summary>
    /// The one somebody is phoning about is the one that has been running longest, so it
    /// is first. Two with nothing running are ordered by who has been in longest.
    /// </summary>
    [Fact]
    public async Task Sessions_come_back_with_the_longest_running_first()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var since = new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient
        {
            OnSessions = _ =>
            [
                Session(1, "idle-newer@cronus.example", since: since.AddHours(2)),
                Session(2, "posting@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
                Session(3, "idle-older@cronus.example", since: since),
                Session(4, "saving@cronus.example", running: TimeSpan.FromSeconds(3)),
            ],
        };

        await using var ctx = _db.NewContext();
        var sessions = await Svc(ctx, TokenOk(), admin).ListEnvironmentSessionsAsync(projectId, envId);

        sessions.Select(s => s.SessionId).Should().Equal(2, 4, 3, 1);
    }

    [Fact]
    public async Task Nobody_signed_in_is_an_empty_list_and_not_a_refusal()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        await using var ctx = _db.NewContext();
        (await Svc(ctx, TokenOk(), new FakeAdminClient()).ListEnvironmentSessionsAsync(projectId, envId))
            .Should().BeEmpty();
    }

    /// <summary>
    /// Reading who is signed in is a read, so it follows the solution's visibility: a
    /// Read-only solution is seen by the whole organisation, so its environments are
    /// read by all of them. Ending one of those sessions signs somebody out, so that
    /// stays with the people who manage it - which on this level is its teams, not
    /// everyone. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    [Fact]
    public async Task A_colleague_reads_a_read_only_solutions_sessions_but_cannot_end_one()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9778, "onlooker3@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9778;
        var admin = new FakeAdminClient { OnSessions = _ => [Session(47, "ola@cronus.example")] };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        (await svc.ListEnvironmentSessionsAsync(projectId, envId))
            .Select(x => x.SessionId).Should().Equal(47);
        await ((Func<Task>)(() => svc.CancelEnvironmentSessionAsync(projectId, envId, 47)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        admin.Cancelled.Should().BeEmpty("nothing reached the customer's tenant");
    }

    /// <summary>
    /// The other half of the same rule: on a Private solution the teams are the whole
    /// answer, and somebody outside them reads nothing - not the sessions, not the
    /// operations, not the panel.
    /// </summary>
    [Fact]
    public async Task Someone_outside_a_private_solutions_teams_reads_none_of_its_environment()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9779, "outsider@example.com", UserRole.User);
        await NarrowAsync(projectId, ProjectVisibility.Private);
        _db.OrgContext.CurrentUserId = 9779;
        var admin = new FakeAdminClient { OnSessions = _ => [Session(47, "ola@cronus.example")] };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        await ((Func<Task>)(() => svc.ListEnvironmentSessionsAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.ListEnvironmentOperationsAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.GetEnvironmentPanelAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    /// <summary>
    /// A colleague who may see the solution but not manage it sees what is installed;
    /// making the customer's tenant answer again is the one part of the panel that stays
    /// a manager's call, because it is the only one that costs the customer anything.
    /// </summary>
    [Fact]
    public async Task A_colleague_reads_a_read_only_solutions_panel_but_cannot_force_a_refresh()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9780, "colleague-reader@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9780;
        var admin = new FakeAdminClient();
        var apps = new FakeAppManagementClient { OnInstalled = () => [App("CRONUS Toolbox")] };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin, apps);

        (await svc.GetEnvironmentPanelAsync(projectId, envId))
            .InstalledApps.Select(a => a.Name).Should().Equal("CRONUS Toolbox");
        await ((Func<Task>)(() => svc.GetEnvironmentPanelAsync(projectId, envId, forceRefresh: true)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    /// <summary>
    /// Business Central's own record of what happened to the environment is a read like
    /// the others, so it follows the solution rather than the manage axis.
    /// </summary>
    [Fact]
    public async Task A_colleague_reads_a_read_only_solutions_operations()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9781, "operations-reader@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9781;
        var admin = new FakeAdminClient { OnOperations = _ => [Operation("update", 3)] };

        await using var ctx = _db.NewContext();
        (await Svc(ctx, TokenOk(), admin).ListEnvironmentOperationsAsync(projectId, envId))
            .Select(o => o.Type).Should().Equal("update");
    }

    /// <summary>
    /// Narrows the solution to <paramref name="visibility"/> with one team the test's
    /// outsiders are not on. A Public solution is managed by everyone in the
    /// organisation, so it is not a test bed for "somebody who cannot manage this":
    /// Read-only is the level where seeing and managing part company, and Private is
    /// where seeing stops too.
    /// </summary>
    private async Task NarrowAsync(int projectId, ProjectVisibility visibility = ProjectVisibility.ReadOnly)
    {
        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId, Name = $"Keepers {projectId}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        ctx.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id, CreatedAt = DateTime.UtcNow,
        });
        var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
        project.Visibility = visibility;
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The history line is written from the session itself, not from its id - "cancelled
    /// session 47" answers nothing a week later, and the list is never stored, so this
    /// line is the only lasting record of it.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_names_whose_it_was_and_what_it_was_doing_in_the_history()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnSessions = _ =>
            [
                Session(47, "ola@cronus.example", running: TimeSpan.FromMinutes(62), currentObject: "Post Sales Documents"),
                Session(48, "kari@cronus.example"),
            ],
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).CancelEnvironmentSessionAsync(projectId, envId, 47);

        admin.Cancelled.Should().ContainSingle().Which.Should().Be(("Production", 47));

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        entry.Kind.Should().Be(UpgradeActionKind.CancelSession);
        entry.Status.Should().Be(UpgradeActionStatus.Sent, "a record, never something for the worker to fire");
        entry.Outcome.Should().Be("Ended session 47 on Production.");
        entry.Outcome.Should().NotContain("ola@cronus.example",
            "who was signed in to a customer's system is shown and forgotten; the record is that we ended one, and which");
        entry.RequestedBy.Should().Contain("owner@example.com");
    }

    /// <summary>
    /// The likeliest failure of all: the list is always a little older than the click. It
    /// is answered here, before anything is sent, so nobody gets a wire 404.
    /// </summary>
    [Fact]
    public async Task A_session_that_has_ended_since_the_list_was_read_is_refused_with_a_sentence()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient { OnSessions = _ => [Session(48, "kari@cronus.example")] };

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).CancelEnvironmentSessionAsync(projectId, envId, 47);
            var thrown = await act.Should().ThrowAsync<PlanValidationException>();
            thrown.Which.Errors["Sessions"].Should().Contain("no longer signed in to Production");
        }

        admin.Cancelled.Should().BeEmpty();
        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId))
            .Should().BeFalse("nothing happened, so nothing is recorded");
    }

    [Fact]
    public async Task A_refusal_from_business_central_comes_back_as_a_field_keyed_sentence()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnSessions = _ => [Session(47, "ola@cronus.example")],
            CancelSessionThrows = new BcApiException(
                HttpStatusCode.NotFound,
                "That session had already ended. Refresh the list to see who is signed in now."),
        };

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).CancelEnvironmentSessionAsync(projectId, envId, 47);
            var thrown = await act.Should().ThrowAsync<PlanValidationException>();
            thrown.Which.Errors["Sessions"].Should().Contain("already ended");
        }

        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_read_business_central_refuses_comes_back_as_a_field_keyed_sentence()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            SessionsThrows = new BcApiException(HttpStatusCode.Forbidden, "The Admin Center API returned 403."),
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).ListEnvironmentSessionsAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Sessions");
    }

    [Fact]
    public async Task Refresh_updates_the_fetched_detail_and_leaves_the_users_own_settings_alone()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        await using (var seed = _db.NewContext())
        {
            seed.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = "PROD", Type = "Production",
                UpdateWindowStart = new TimeOnly(22, 0), UpdateWindowEnd = new TimeOnly(6, 0),
                Status = "Active", Version = "27.4.0.0", FetchedAt = DateTime.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        var tenant = Guid.NewGuid();
        var admin = new FakeAdminClient
        {
            OnList = () => new[]
            {
                new BcEnvironment("PROD", "Production")
                {
                    FriendlyName = "CRONUS Production",
                    ApplicationFamily = "BusinessCentral",
                    Status = "Upgrading",
                    CountryCode = "DK",
                    AadTenantId = tenant,
                    WebClientLoginUrl = "https://businesscentral.dynamics.com/x/PROD",
                    Version = "27.5.5.15",
                },
            },
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id && e.Name == "PROD");

        // Fetched fields move to what the API just said...
        row.Status.Should().Be("Upgrading");
        row.StatusFetchedAt.Should().NotBeNull();
        row.Version.Should().Be("27.5.5.15");
        row.FriendlyName.Should().Be("CRONUS Production");
        row.ApplicationFamily.Should().Be("BusinessCentral");
        row.CountryCode.Should().Be("DK");
        row.AadTenantId.Should().Be(tenant);
        row.WebClientLoginUrl.Should().Be("https://businesscentral.dynamics.com/x/PROD");

        // ...and the user's own settings on the same row are untouched.
        row.UpdateWindowStart.Should().Be(new TimeOnly(22, 0));
        row.UpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
    }

    [Fact]
    public async Task Refresh_mirrors_each_environments_business_central_update_window()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnUpdateSettings = _ => new BcUpdateSettings(new TimeOnly(2, 0), new TimeOnly(6, 0), "Romance Standard Time"),
        };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.BcUpdateWindowStart.Should().Be(new TimeOnly(2, 0));
        row.BcUpdateWindowEnd.Should().Be(new TimeOnly(6, 0));
        row.BcUpdateWindowTimeZoneId.Should().Be("Romance Standard Time", "the Windows id is what a write takes back");
        row.BcUpdateWindowTimeZoneIana.Should().Be("Europe/Paris", "display maths needs the IANA form on Linux");
        row.BcUpdateWindowFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_survives_an_environment_whose_update_window_cannot_be_read()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production"), new BcEnvironment("Sandbox", "Sandbox") },
            // One environment answers, the other refuses. The refusal must not cost us
            // the environment list, which is what the Refresh is actually for.
            OnUpdateSettings = name => name == "Sandbox"
                ? throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "denied")
                : new BcUpdateSettings(new TimeOnly(1, 0), new TimeOnly(5, 0), "UTC"),
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(id);

        result.IsSuccess.Should().BeTrue("one environment's settings call is not the refresh");
        result.EnvironmentCount.Should().Be(2);

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking().Where(e => e.ProjectId == id).ToListAsync();
        rows.Single(e => e.Name == "Production").BcUpdateWindowFetchedAt.Should().NotBeNull();
        rows.Single(e => e.Name == "Sandbox").BcUpdateWindowFetchedAt
            .Should().BeNull("a failed read leaves the row alone rather than stamping a time it never got");
    }

    // ── The mirrored next platform update (what the fleet page lists) ─────

    /// <summary>One update record, with only the fields a mirror test cares about set.</summary>
    private static BcEnvironmentUpdate Update(
        string version, bool available = true, bool selected = false,
        DateTimeOffset? selectedAt = null, DateTimeOffset? latest = null,
        bool ignoresWindow = false, string status = "Scheduled", string type = "Minor")
        => new(version, available, selected, status, type, selectedAt, latest, ignoresWindow,
            RolloutStatus: "Released", ExpectedMonth: null, ExpectedYear: null);

    private async Task<OeProjectEnvironment> RefreshAndReadRowAsync(int projectId, FakeAdminClient admin)
    {
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(projectId);

        await using var verify = _db.NewContext();
        return await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == projectId);
    }

    [Fact]
    public async Task Refresh_mirrors_the_selected_update_even_when_a_newer_one_is_available()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var scheduled = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[]
            {
                Update("27.6", selected: true, selectedAt: scheduled, latest: latest, status: "Scheduled", type: "Minor"),
                Update("28.0", status: "Available", type: "Major"),
            },
        };

        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().Be("27.6", "the customer's chosen slot is the answer, not the newest offer");
        row.BcNextUpdateType.Should().Be("Minor");
        row.BcNextUpdateStatus.Should().Be("Scheduled", "the API's own wording is stored verbatim");
        row.BcNextUpdateDate.Should().Be(scheduled.UtcDateTime);
        row.BcNextUpdateLatestDate.Should().Be(latest.UtcDateTime);
        row.BcNextUpdateIgnoresWindow.Should().BeFalse();
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_mirrors_the_newest_available_update_when_none_is_selected()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            // 10.0 beats 9.9 numerically; a string compare would pick 9.9 and quietly
            // mirror last year's update as the next one.
            OnEnvironmentUpdates = _ => new[]
            {
                Update("9.9"),
                Update("10.0"),
                Update("11.0", available: false, status: "NotAvailable"),
            },
        };

        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().Be("10.0",
            "10.0 is newer than 9.9, and an unavailable version has no date to schedule");
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_refresh_mirrors_each_environments_size_and_the_tenants_allowance()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnStorage = () => new BcTenantStorage(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["production"] = 52428800 }, 83886080),
        };

        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcDatabaseKb.Should().Be(52428800);
        await using var verify = _db.NewContext();
        var project = await verify.OeProjects.AsNoTracking().SingleAsync(p => p.Id == id);
        project.BcStorageQuotaKb.Should().Be(83886080);
        project.BcStorageFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Storage_business_central_refuses_to_report_leaves_the_last_figures_and_the_rest_of_the_refresh_alone()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnStorage = () => new BcTenantStorage(new Dictionary<string, long> { ["Production"] = 1000 }, 5000),
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: DateTimeOffset.UtcNow) },
        };
        await RefreshAndReadRowAsync(id, admin);

        admin.OnStorage = () => throw new BcApiException(HttpStatusCode.Forbidden, "denied");
        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcDatabaseKb.Should().Be(1000, "a failed read costs the freshness, never the figures");
        row.BcNextUpdateVersion.Should().Be("27.6", "and it does not take the other mirrors down with it");
    }

    [Fact]
    public async Task A_refresh_mirrors_what_is_installed_and_a_later_one_brings_it_in_line()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Production", "Production") } };
        var core = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { Installed(core, "Continia Core", "28.4"), Installed(gone, "Old Add-on", "1.0") },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin, apps).RefreshEnvironmentsAsync(id);
        apps.OnInstalled = () => new[] { Installed(core, "Continia Core", "28.5") };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin, apps).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        var mirrored = await verify.OeEnvironmentApps.AsNoTracking().ToListAsync();
        mirrored.Should().ContainSingle().Which.Should().BeEquivalentTo(new { AppId = core, Name = "Continia Core", Version = "28.5" });
    }

    [Fact]
    public async Task An_answer_with_no_apps_in_it_is_a_failed_read_and_keeps_the_last_good_mirror()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Production", "Production") } };
        var apps = new FakeAppManagementClient { OnInstalled = () => new[] { Installed(Guid.NewGuid(), "Continia Core", "28.4") } };
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin, apps).RefreshEnvironmentsAsync(id);

        apps.OnInstalled = Array.Empty<BcInstalledApp>;
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin, apps).RefreshEnvironmentsAsync(id);

        await using var verify = _db.NewContext();
        (await verify.OeEnvironmentApps.AsNoTracking().CountAsync()).Should().Be(1,
            "an environment always has the base application, so nothing at all means the read went wrong");
    }

    private static BcInstalledApp Installed(Guid appId, string name, string version) =>
        new(appId, name, "Continia Software", version, "Installed", "global", true, null, string.Empty);

    [Fact]
    public async Task Operations_come_back_newest_first_whatever_order_business_central_used()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnOperations = _ => new[] { Operation("restart", 1), Operation("update", 3), Operation("modify", 2) },
        };
        var env = await RefreshAndReadRowAsync(id, admin);

        await using var read = _db.NewContext();
        var operations = await Svc(read, TokenOk(), admin).ListEnvironmentOperationsAsync(id, env.Id);

        operations.Select(o => o.Type).Should().Equal("update", "modify", "restart");
    }

    [Fact]
    public async Task Operations_business_central_refuses_come_back_as_a_sentence_for_the_page()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());
        var admin = new FakeAdminClient { OnList = () => new[] { new BcEnvironment("Production", "Production") } };
        var env = await RefreshAndReadRowAsync(id, admin);
        admin.OnOperations = _ => throw new BcApiException(HttpStatusCode.Forbidden, "Business Central refused: no admin access.");

        await using var read = _db.NewContext();
        var act = () => Svc(read, TokenOk(), admin).ListEnvironmentOperationsAsync(id, env.Id);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Values.Should().Contain("Business Central refused: no admin access.");
    }

    private static BcEnvironmentOperation Operation(string type, int day) => new(
        Guid.NewGuid().ToString(), type, "succeeded",
        new DateTimeOffset(2026, 9, day, 8, 0, 0, TimeSpan.Zero), null, null,
        string.Empty, string.Empty, new Dictionary<string, string>());

    [Fact]
    public async Task Refresh_clears_the_mirror_and_stamps_it_when_there_is_no_update()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: DateTimeOffset.UtcNow) },
        };
        await RefreshAndReadRowAsync(id, admin);

        // The customer's update ran; the list is now empty.
        admin.OnEnvironmentUpdates = _ => Array.Empty<BcEnvironmentUpdate>();
        var row = await RefreshAndReadRowAsync(id, admin);

        row.BcNextUpdateVersion.Should().BeNull();
        row.BcNextUpdateType.Should().BeNull();
        row.BcNextUpdateStatus.Should().BeNull();
        row.BcNextUpdateDate.Should().BeNull();
        row.BcNextUpdateLatestDate.Should().BeNull();
        row.BcNextUpdateIgnoresWindow.Should().BeNull();
        row.BcNextUpdateFetchedAt.Should().NotBeNull(
            "an empty list is a successful read saying 'nothing scheduled', not 'never asked'");
    }

    [Fact]
    public async Task Refresh_leaves_the_mirror_alone_when_the_updates_call_fails()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var scheduled = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: scheduled) },
        };
        var before = await RefreshAndReadRowAsync(id, admin);
        before.BcNextUpdateFetchedAt.Should().NotBeNull();

        admin.OnEnvironmentUpdates = _ => throw new BcApiException(HttpStatusCode.Forbidden, "denied");
        var after = await RefreshAndReadRowAsync(id, admin);

        after.BcNextUpdateVersion.Should().Be("27.6", "a failed read degrades to stale, never to blank");
        after.BcNextUpdateDate.Should().Be(scheduled.UtcDateTime);
        after.BcNextUpdateFetchedAt.Should().Be(before.BcNextUpdateFetchedAt,
            "the age of the answer is the age of the last successful read");
    }

    [Fact]
    public async Task Unattended_refresh_mirrors_without_claiming_the_connection_was_verified()
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") },
            OnEnvironmentUpdates = _ => new[] { Update("27.6", selected: true, selectedAt: DateTimeOffset.UtcNow) },
        };

        BcConnectionTestResult result;
        await using (var ctx = _db.NewContext())
            result = await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsUnattendedAsync(id);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.ProjectId == id);
        row.Name.Should().Be("Production", "the sweep upserts the environment list like a Refresh does");
        row.BcNextUpdateVersion.Should().Be("27.6");

        var verified = await verify.OeProjects.AsNoTracking().Where(p => p.Id == id)
            .Select(p => p.BcConnectionVerifiedAt).SingleAsync();
        verified.Should().BeNull("a sweep nobody asked for is not the consultant's own connection test");
    }

    // ── The environment panel (live reads, never cached) ──────────────────

    /// <summary>Seeds a project with one environment and returns both ids.</summary>
    private async Task<(int ProjectId, int EnvironmentId)> SeedEnvironmentAsync(string name = "Production")
    {
        var id = await SeedProjectAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        await using var seed = _db.NewContext();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = id, Name = name,
            Type = "Production", ApplicationFamily = "BusinessCentral", FetchedAt = DateTime.UtcNow,
        };
        seed.OeProjectEnvironments.Add(env);
        await seed.SaveChangesAsync();
        return (id, env.Id);
    }

    private static BcInstalledApp App(string name, string appType = "tenant", Guid? appId = null) => new(
        AppId: appId ?? Guid.NewGuid(), Name: name, Publisher: "CRONUS A/S", Version: "1.0.0.0",
        State: "Installed", AppType: appType, CanBeUninstalled: true,
        LastOperationId: null, LastUpdateAttemptResult: string.Empty);

    private static BcScheduledPteOperation Scheduled(Guid appId, string version = "2.0.0.0") => new(
        Id: Guid.NewGuid(), AppId: appId, Type: "Install", Status: BcAppOperationStatus.Scheduled,
        RawStatus: "scheduled", TargetAppVersion: version, ScheduleKind: BcDeploymentSchedule.UpdateWindow,
        Name: "CRONUS Workbench", Publisher: "CRONUS A/S", SyncMode: BcSyncMode.Add,
        LanguageId: string.Empty, CreatedOn: DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_panel_reads_all_four_sections_live()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Workbench"), App("Some Marketplace App", "global") },
            OnAvailable = () => new[] { new BcAvailableAppUpdate(Guid.NewGuid(), "Some Marketplace App", "Vendor", "3.0.0.0", Array.Empty<BcAppUpdateRequirement>()) },
            OnScheduled = () => new[] { Scheduled(Guid.NewGuid()) },
        };
        var admin = new FakeAdminClient
        {
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, true, "scheduled", "GA",
                    DateTimeOffset.UtcNow.AddDays(7), null, false, "Active", null, null),
            },
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), admin, apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.EnvironmentName.Should().Be("Production");
        panel.InstalledApps.Should().HaveCount(2);
        panel.AvailableUpdates.Should().ContainSingle();
        panel.ScheduledInstalls.Should().ContainSingle();
        panel.EnvironmentUpdates.Should().ContainSingle();
        panel.InstalledAppsError.Should().BeNull();
    }

    [Fact]
    public async Task One_denied_section_does_not_blank_the_others()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Workbench") },
            // The available-updates read is denied; the rest must survive it.
            OnAvailable = () => throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "denied"),
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.AvailableUpdatesError.Should().NotBeNull().And.Subject.Should().Contain("Marketplace");
        panel.AvailableUpdates.Should().BeEmpty();
        panel.InstalledApps.Should().ContainSingle("one refusal is not the whole panel");
        panel.InstalledAppsError.Should().BeNull();
    }

    [Fact]
    public async Task The_panel_marks_the_apps_this_workbench_released_here()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var ours = Guid.NewGuid();
        await SeedDeliveredAppAsync(projectId, "Production", ours);

        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Workbench", "tenant", ours), App("Someone Else's PTE") },
        };

        await using var ctx = _db.NewContext();
        var panel = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps).GetEnvironmentPanelAsync(projectId, envId);

        panel.ReleasedAppIds.Should().ContainSingle().Which.Should().Be(ours,
            "the panel can then say which pending install is the consultant's own");
    }

    [Fact]
    public async Task Cancelling_a_scheduled_install_names_the_version_and_schedule()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var appId = Guid.NewGuid();
        var apps = new FakeAppManagementClient();

        await using var ctx = _db.NewContext();
        await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .CancelScheduledInstallAsync(projectId, envId, appId, "2.0.0.0", BcDeploymentSchedule.UpdateWindow);

        // All three identify the entry; Business Central needs every one of them.
        apps.Removed.Should().Be((appId, "2.0.0.0", BcDeploymentSchedule.UpdateWindow));
    }

    [Fact]
    public async Task A_refused_cancel_reads_as_something_a_consultant_can_act_on()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            RemoveThrows = new BcApiException(System.Net.HttpStatusCode.NotFound,
                "The Admin Center API returned 404. ResourceDoesNotExist"),
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .CancelScheduledInstallAsync(projectId, envId, Guid.NewGuid(), "2.0.0.0", BcDeploymentSchedule.UpdateWindow);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Environment"];
        error.Should().Contain("didn't cancel");
    }

    // ── Updating an AppSource app ─────────────────────────────────────────

    private static readonly Guid WaitingAppId = Guid.NewGuid();

    private static FakeAppManagementClient AppsWithWaiting(params BcAppUpdateRequirement[] requirements) => new()
    {
        OnAvailable = () => new[]
        {
            new BcAvailableAppUpdate(WaitingAppId, "Continia Core", "Continia Software", "28.5.0.1", requirements),
        },
    };

    // Every timing is a booking, as for an upload (#1001): nothing reaches Business
    // Central at the form, and the send re-reads the waiting updates before it goes.

    [Fact]
    public async Task An_app_update_for_now_is_booked_with_what_the_worker_needs_and_sends_nothing_yet()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = AppsWithWaiting();

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now);

        outcome.Timing.Should().Be(UploadAppTiming.Now);
        outcome.RunsAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime);
        apps.Updated.Should().BeNull("the worker sends it, never the page request");

        await using var read = _db.NewContext();
        var row = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        row.Kind.Should().Be(UpgradeActionKind.UpdateApp);
        row.Status.Should().Be(UpgradeActionStatus.Pending);
        row.BcAppId.Should().Be(WaitingAppId);
        row.AppName.Should().Be("Continia Core");
        row.TargetVersion.Should().Be("28.5.0.1");
        row.PrerequisiteAppIds.Should().BeEmpty();
        row.PackageContent.Should().BeNull("there is no package: the app is Business Central's own");
    }

    [Fact]
    public async Task An_app_update_for_a_picked_time_is_booked_for_that_slot_and_a_time_that_has_gone_is_refused()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = AppsWithWaiting();
        var slot = _clock.GetUtcNow().AddHours(8);

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        var outcome = await svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.AtTime, slot);
        outcome.RunsAtUtc.Should().Be(slot.UtcDateTime);

        var past = () => svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.AtTime, _clock.GetUtcNow().AddHours(-1));
        (await past.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ExecuteAt");
    }

    [Fact]
    public async Task An_app_update_waits_for_the_delivery_window_like_an_upload()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(4, 0));
        // Outside the window, so both bookings wait for its next 22:00 rather than running now.
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        if (UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(4, 0), copenhagen, _clock.GetUtcNow().UtcDateTime))
            _clock.Advance(TimeSpan.FromHours(8));
        var apps = AppsWithWaiting();

        await using var ctx = _db.NewContext();
        var update = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.DeliveryWindow);

        await using var ctx2 = _db.NewContext();
        var upload = await Svc(ctx2, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.DeliveryWindow);

        update.Timing.Should().Be(UploadAppTiming.DeliveryWindow);
        update.RunsAtUtc.Should().Be(upload.RunsAtUtc, "both book the same slot for the same answer");
        update.RunsAtUtc.Should().BeAfter(_clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task An_app_update_is_refused_for_a_version_business_central_is_not_offering()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = AppsWithWaiting();

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        var stale = () => svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.4.0.0", UploadAppTiming.Now);
        (await stale.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("28.5.0.1");

        var unknown = () => svc.BookAppUpdateAsync(projectId, envId, Guid.NewGuid(), "28.5.0.1", UploadAppTiming.Now);
        (await unknown.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("no longer has an update waiting");

        var blank = () => svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, " ", UploadAppTiming.Now);
        (await blank.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("App");

        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId))
            .Should().BeFalse("nothing is booked on a refusal");
    }

    [Fact]
    public async Task An_app_that_waits_for_another_is_not_booked_without_agreeing_to_it()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = AppsWithWaiting(new BcAppUpdateRequirement(Guid.NewGuid(), "Continia System Application", "Continia Software", "28.5.0.0", "update"));

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"]
            .Should().Contain("Continia System Application");
    }

    [Fact]
    public async Task An_app_that_waits_for_others_is_booked_with_them_once_each_one_is_confirmed()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var apps = AppsWithWaiting(
            new BcAppUpdateRequirement(first, "Continia System Application", "Continia Software", "28.5.0.0", "update"),
            new BcAppUpdateRequirement(second, "Continia Connector App", "Continia Software", "28.5.0.0", "install"));

        await using var ctx = _db.NewContext();
        await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now, confirmedPrerequisiteAppIds: new[] { first, second });

        await using var read = _db.NewContext();
        var row = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        row.PrerequisiteAppIds.Should().BeEquivalentTo(new[] { first, second }, "what the person was shown is what the send may bring along");
    }

    [Fact]
    public async Task A_prerequisite_nobody_confirmed_stops_the_booking()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var seen = Guid.NewGuid();
        var apps = AppsWithWaiting(
            new BcAppUpdateRequirement(seen, "Continia System Application", "Continia Software", "28.5.0.0", "update"),
            new BcAppUpdateRequirement(Guid.NewGuid(), "Continia Connector App", "Continia Software", "28.5.0.0", "update"));

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now, confirmedPrerequisiteAppIds: new[] { seen });

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"];
        error.Should().Contain("Continia Connector App").And.NotContain("Continia System Application");
    }

    [Fact]
    public async Task A_second_booking_for_an_app_already_booked_is_refused()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = AppsWithWaiting();

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        await svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.AtTime, _clock.GetUtcNow().AddHours(8));

        var again = () => svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now);
        (await again.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("already booked");
    }

    [Fact]
    public async Task An_app_already_moving_with_another_booked_update_is_not_booked_again()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var systemApp = Guid.NewGuid();
        var apps = new FakeAppManagementClient
        {
            OnAvailable = () => new[]
            {
                new BcAvailableAppUpdate(WaitingAppId, "Continia Core", "Continia Software", "28.5.0.1",
                    new[] { new BcAppUpdateRequirement(systemApp, "Continia System Application", "Continia Software", "28.5.0.0", "update") }),
                new BcAvailableAppUpdate(systemApp, "Continia System Application", "Continia Software", "28.5.0.0", Array.Empty<BcAppUpdateRequirement>()),
            },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        await svc.BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.AtTime,
            _clock.GetUtcNow().AddHours(8), new[] { systemApp });

        var prerequisiteAlone = () => svc.BookAppUpdateAsync(projectId, envId, systemApp, "28.5.0.0", UploadAppTiming.Now);
        (await prerequisiteAlone.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"]
            .Should().Contain("Continia Core").And.Contain("Cancel that booking");
    }

    [Fact]
    public async Task Sending_a_booked_update_runs_it_now_brings_the_agreed_apps_and_waits_for_it()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var prerequisite = Guid.NewGuid();
        var apps = AppsWithWaiting(new BcAppUpdateRequirement(prerequisite, "Continia System Application", "Continia Software", "28.5.0.0", "update"));
        apps.OnOperation = id => new BcAppOperation(
            id, WaitingAppId, "update", BcAppOperationStatus.Failed, "failed",
            string.Empty, "28.5.0.1", null, "Continia Core couldn't be upgraded.", "UpgradeFailed", string.Empty,
            false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        svc.UploadPollDelay = TimeSpan.Zero;
        await svc.GetEnvironmentPanelAsync(projectId, envId);
        BcAppOperation? accepted = null;
        var result = await svc.SendBookedUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", new[] { prerequisite },
            (op, _) => { accepted = op; return Task.CompletedTask; }, CancellationToken.None);

        apps.Updated.Should().Be((WaitingAppId, "28.5.0.1", false), "the booking is the schedule; Business Central's window is never handed over");
        apps.UpdatedWithDependencies.Should().BeTrue();
        accepted.Should().NotBeNull();
        result.Completed.Should().BeFalse("the send waited for Business Central's answer");
        result.Message.Should().Contain("couldn't be upgraded");
        _panelCache.Get(projectId, envId).Should().BeNull("the page must re-read after our own write, not show the old list");
    }

    [Fact]
    public async Task Sending_a_booked_update_is_refused_when_business_central_has_moved_on_since_it_was_booked()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var agreed = Guid.NewGuid();

        // A newer version is waiting now.
        await using (var ctx = _db.NewContext())
        {
            var newer = new FakeAppManagementClient
            {
                OnAvailable = () => new[] { new BcAvailableAppUpdate(WaitingAppId, "Continia Core", "Continia Software", "28.6.0.0", Array.Empty<BcAppUpdateRequirement>()) },
            };
            var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), newer)
                .SendBookedUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", Array.Empty<Guid>(), null, CancellationToken.None);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("28.6.0.0");
            newer.Updated.Should().BeNull();
        }

        // A prerequisite nobody agreed to has appeared.
        await using (var ctx = _db.NewContext())
        {
            var widened = AppsWithWaiting(
                new BcAppUpdateRequirement(agreed, "Continia System Application", "Continia Software", "28.5.0.0", "update"),
                new BcAppUpdateRequirement(Guid.NewGuid(), "Continia Connector App", "Continia Software", "28.5.0.0", "install"));
            var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), widened)
                .SendBookedUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", new[] { agreed }, null, CancellationToken.None);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("Continia Connector App");
            widened.Updated.Should().BeNull();
        }
    }

    // ── Uploading apps by hand ────────────────────────────────────────────
    // Every timing is a booking: nothing reaches Business Central at the form, one
    // pending row per app carries the package, and the worker sends them in order.

    private static UploadPackage Pkg(string name, params byte[] bytes) => new(bytes, name);

    [Fact]
    public async Task An_upload_for_now_is_booked_for_a_slot_that_has_already_come_and_sends_nothing_yet()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient();

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        var outcome = await svc.InstallUploadedAppsAsync(projectId, envId,
            new[] { Pkg(@"C:\Downloads\Partner_Thing_1.0.0.0.app", 1, 2, 3) }, UploadAppTiming.Now);

        apps.Installed.Should().BeNull("the worker sends it, so an install that takes minutes never runs inside a page request");
        outcome.Timing.Should().Be(UploadAppTiming.Now);
        outcome.Count.Should().Be(1);
        outcome.RunsAtUtc.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        entry.Kind.Should().Be(UpgradeActionKind.UploadApp);
        entry.Status.Should().Be(UpgradeActionStatus.Pending);
        entry.PackageFileName.Should().Be("Partner_Thing_1.0.0.0.app", "the path is dropped");
        entry.PackageContent.Should().Equal(new byte[] { 1, 2, 3 });
        entry.BatchId.Should().BeNull("a single app is not a batch");
    }

    [Fact]
    public async Task Several_apps_are_booked_as_one_batch_in_the_order_given()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId,
                new[] { Pkg("Core.app", 1), Pkg("Connector.app", 2), Pkg("Reports.app", 3) }, UploadAppTiming.Now);

        outcome.Count.Should().Be(3);
        await using var read = _db.NewContext();
        var rows = await read.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.EnvironmentId == envId).OrderBy(a => a.BatchOrder).ToListAsync();
        rows.Select(r => r.PackageFileName).Should().Equal("Core.app", "Connector.app", "Reports.app");
        rows.Select(r => r.BatchOrder).Should().Equal(0, 1, 2);
        rows.Select(r => r.BatchId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        rows.Should().OnlyContain(r => r.Status == UpgradeActionStatus.Pending && r.ExecuteAfter == rows[0].ExecuteAfter);
    }

    [Fact]
    public async Task An_upload_booked_for_a_picked_time_is_held_here_with_its_package()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient();
        var slot = _clock.GetUtcNow().AddHours(3);

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1, 2, 3) }, UploadAppTiming.AtTime, slot);

        apps.Installed.Should().BeNull("a booking sends nothing until its slot");
        outcome.Timing.Should().Be(UploadAppTiming.AtTime);
        outcome.RunsAtUtc.Should().Be(slot.UtcDateTime);

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        entry.Status.Should().Be(UpgradeActionStatus.Pending);
        // Postgres keeps microseconds, so the stored slot can be a few ticks off the one asked for.
        entry.ExecuteAfter.Should().BeCloseTo(slot.UtcDateTime, TimeSpan.FromMilliseconds(1));
        entry.PackageFileName.Should().Be("Partner.app");
        entry.PackageContent.Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task A_picked_time_that_has_passed_is_refused_and_nothing_is_stored()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.AtTime, _clock.GetUtcNow().AddHours(-1));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ExecuteAt"].Should().Contain("hasn't happened yet");
        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId)).Should().BeFalse();
    }

    [Fact]
    public async Task An_upload_for_the_delivery_window_waits_for_its_next_opening_in_the_customers_zone()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SetWindowsAsync(envId, deliveryStart: new TimeOnly(22, 0), deliveryEnd: new TimeOnly(4, 0));
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        // Outside the window, so the booking waits for its next 22:00.
        if (UpdateWindow.IsWithin(new TimeOnly(22, 0), new TimeOnly(4, 0), copenhagen, _clock.GetUtcNow().UtcDateTime))
            _clock.Advance(TimeSpan.FromHours(8));

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.DeliveryWindow);

        outcome.Timing.Should().Be(UploadAppTiming.DeliveryWindow);
        outcome.RunsAtUtc.Should().BeAfter(_clock.GetUtcNow().UtcDateTime);
        var local = TimeZoneInfo.ConvertTimeFromUtc(outcome.RunsAtUtc, copenhagen);
        local.TimeOfDay.Should().Be(new TimeSpan(22, 0, 0), "the window is agreed as a wall clock in the customer's zone, not in UTC");

        await using var read = _db.NewContext();
        var entry = await read.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync(a => a.EnvironmentId == envId);
        entry.ExecuteAfter.Should().BeCloseTo(outcome.RunsAtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task An_upload_while_the_delivery_window_is_open_is_booked_for_now()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, copenhagen));
        await SetWindowsAsync(envId, deliveryStart: local.AddHours(-1), deliveryEnd: local.AddHours(1));

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.DeliveryWindow);

        outcome.Timing.Should().Be(UploadAppTiming.DeliveryWindow);
        outcome.RunsAtUtc.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1), "the window is open, so the next sweep sends it");
    }

    [Fact]
    public async Task An_upload_for_microsofts_window_is_booked_for_its_next_opening_never_handed_over_deferred()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SetWindowsAsync(envId, bcStart: new TimeOnly(1, 0), bcEnd: new TimeOnly(5, 0), bcZone: "America/New_York");
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        if (UpdateWindow.IsWithin(new TimeOnly(1, 0), new TimeOnly(5, 0), newYork, _clock.GetUtcNow().UtcDateTime))
            _clock.Advance(TimeSpan.FromHours(6));
        var apps = new FakeAppManagementClient();

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.BcUpdateWindow);

        apps.Installed.Should().BeNull("Business Central's own queue would pick the order; the workbench books the opening instead");
        outcome.Timing.Should().Be(UploadAppTiming.BcUpdateWindow);
        TimeZoneInfo.ConvertTimeFromUtc(outcome.RunsAtUtc, newYork).TimeOfDay.Should().Be(new TimeSpan(1, 0, 0),
            "Microsoft's window is expressed in the zone the admin centre reports it in");
    }

    [Fact]
    public async Task The_delivery_window_falls_back_to_microsofts_window_when_there_is_none()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SetWindowsAsync(envId, bcStart: new TimeOnly(1, 0), bcEnd: new TimeOnly(5, 0), bcZone: "Europe/Copenhagen");

        await using var ctx = _db.NewContext();
        var outcome = await Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.DeliveryWindow);

        outcome.Timing.Should().Be(UploadAppTiming.BcUpdateWindow, "the page says what actually applied");
    }

    [Theory]
    [InlineData(UploadAppTiming.DeliveryWindow)]
    [InlineData(UploadAppTiming.BcUpdateWindow)]
    public async Task A_window_that_has_not_been_read_is_refused_rather_than_guessed(UploadAppTiming timing)
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, timing);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Timing"].Should().Contain("hasn't been read");
    }

    [Theory]
    [InlineData("Partner.zip", 3, ".app")]
    [InlineData("Partner.app", 0, "empty")]
    [InlineData("Partner.app", BcAppManagementClient.MaxAppBytes + 1, "50 MB")]
    public async Task An_upload_business_central_would_refuse_never_leaves_the_workbench(string fileName, int size, string says)
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { new UploadPackage(new byte[size], fileName) }, UploadAppTiming.Now);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain(says);
        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId)).Should().BeFalse();
    }

    [Fact]
    public async Task Sending_a_booked_upload_waits_for_business_central_to_finish_and_reports_its_refusal()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var apps = new FakeAppManagementClient
        {
            OnOperation = _ => new BcAppOperation(
                Guid.NewGuid(), Guid.NewGuid(), "install", BcAppOperationStatus.Failed, "failed",
                string.Empty, "1.0.0.0", null, "It needs Continia Core 28.0.0.0, which isn't installed.", "MissingDependency", string.Empty,
                false, "app", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow),
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);
        svc.UploadPollDelay = TimeSpan.Zero;
        BcAppOperation? accepted = null;
        var result = await svc.SendBookedUploadAsync(projectId, envId, new byte[] { 1 }, "Partner.app",
            (op, _) => { accepted = op; return Task.CompletedTask; }, CancellationToken.None);

        accepted.Should().NotBeNull("the caller is told what Business Central accepted before the install is polled");

        apps.Installed!.Value.Schedule.Should().Be(BcDeploymentSchedule.Immediate);
        result.Completed.Should().BeFalse();
        result.Message.Should().Contain("Continia Core");
    }

    [Fact]
    public async Task Someone_who_does_not_manage_the_solution_cannot_upload_an_app_to_it()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9778, "uploader@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9778;

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, new[] { Pkg("Partner.app", 1) }, UploadAppTiming.Now);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task More_apps_than_one_upload_takes_are_refused_before_anything_is_stored()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var packages = Enumerable.Range(1, ProjectConnectionService.MaxUploadBatch + 1).Select(i => Pkg($"App{i}.app", 1)).ToList();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), new FakeAppManagementClient())
            .InstallUploadedAppsAsync(projectId, envId, packages, UploadAppTiming.Now);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["App"].Should().Contain("at most");
        await using var read = _db.NewContext();
        (await read.OeEnvironmentUpgradeActions.AsNoTracking().AnyAsync(a => a.EnvironmentId == envId)).Should().BeFalse();
    }

    private async Task SetWindowsAsync(int envId,
        TimeOnly? deliveryStart = null, TimeOnly? deliveryEnd = null,
        TimeOnly? bcStart = null, TimeOnly? bcEnd = null, string? bcZone = null)
    {
        await using var ctx = _db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
        env.UpdateWindowStart = deliveryStart;
        env.UpdateWindowEnd = deliveryEnd;
        env.BcUpdateWindowStart = bcStart;
        env.BcUpdateWindowEnd = bcEnd;
        env.BcUpdateWindowTimeZoneIana = bcZone;
        if (bcStart is not null) env.BcUpdateWindowFetchedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Someone_who_does_not_manage_the_solution_cannot_update_its_apps()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9777, "stranger@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9777;
        var apps = AppsWithWaiting();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), new FakeAdminClient(), apps)
            .BookAppUpdateAsync(projectId, envId, WaitingAppId, "28.5.0.1", UploadAppTiming.Now);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        apps.Updated.Should().BeNull();
    }

    [Fact]
    public async Task A_second_open_inside_the_window_does_not_ask_business_central_again()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var reads = 0;
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => { reads++; return new[] { App("CRONUS Workbench") }; },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        var first = await svc.GetEnvironmentPanelAsync(projectId, envId);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await svc.GetEnvironmentPanelAsync(projectId, envId);

        reads.Should().Be(1, "expanding the same environment again is the traffic the cache exists to remove");
        second.FetchedAtUtc.Should().Be(first.FetchedAtUtc,
            "a cached panel reports when it was really read, not when it was served");
    }

    [Fact]
    public async Task Refresh_bypasses_the_cache()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var reads = 0;
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => { reads++; return new[] { App("CRONUS Workbench") }; },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        await svc.GetEnvironmentPanelAsync(projectId, envId);
        await svc.GetEnvironmentPanelAsync(projectId, envId, forceRefresh: true);

        reads.Should().Be(2, "Refresh is the consultant's way past a cached answer");
    }

    [Fact]
    public async Task The_cached_panel_lapses_once_its_window_has_passed()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var reads = 0;
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => { reads++; return new[] { App("CRONUS Workbench") }; },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        await svc.GetEnvironmentPanelAsync(projectId, envId);
        _clock.Advance(BcPanelCache.Ttl);
        await svc.GetEnvironmentPanelAsync(projectId, envId);

        reads.Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_a_scheduled_install_drops_the_cached_panel()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var reads = 0;
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => { reads++; return new[] { App("CRONUS Workbench") }; },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        await svc.GetEnvironmentPanelAsync(projectId, envId);
        await svc.CancelScheduledInstallAsync(
            projectId, envId, Guid.NewGuid(), "2.0.0.0", BcDeploymentSchedule.UpdateWindow);
        await svc.GetEnvironmentPanelAsync(projectId, envId);

        reads.Should().Be(2,
            "a consultant must never be shown a stale panel because of something they just did here");
    }

    [Fact]
    public async Task A_delivery_after_the_cached_read_still_shows_as_ours()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var ours = Guid.NewGuid();
        var apps = new FakeAppManagementClient
        {
            OnInstalled = () => new[] { App("CRONUS Workbench", "tenant", ours) },
        };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), new FakeAdminClient(), apps);

        var before = await svc.GetEnvironmentPanelAsync(projectId, envId);
        before.ReleasedAppIds.Should().BeEmpty();

        // The delivery lands while the panel is still cached. Which apps are ours comes
        // from our own database, so it is re-read on a cache hit rather than frozen.
        await SeedDeliveredAppAsync(projectId, "Production", ours);
        var after = await svc.GetEnvironmentPanelAsync(projectId, envId);

        after.ReleasedAppIds.Should().ContainSingle().Which.Should().Be(ours);
    }

    /// <summary>Records a delivery that put <paramref name="appId"/> onto the environment.</summary>
    private async Task SeedDeliveredAppAsync(int projectId, string environmentName, Guid appId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
        };
        ctx.OeProjectBuilds.Add(build);

        var env = await ctx.OeProjectEnvironments.FirstAsync(e => e.ProjectId == projectId && e.Name == environmentName);
        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Rel " + Guid.NewGuid().ToString("N"),
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId,
            ReleasePipelineId = releasePipeline.Id, ProjectBuildId = build.Id,
            EnvironmentName = environmentName, ScheduledFor = DateTime.UtcNow,
            Status = ProjectDeliveryStatus.HandedOff, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        delivery.Results.Add(new OeProjectDeliveryResult
        {
            OrganizationId = TestDb.DefaultOrgId, Ordering = 0, AppName = "CRONUS Workbench",
            AppVersion = "2.0.0.0", AppId = appId.ToString(), Status = ProjectDeliveryResultStatus.Scheduled,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
    }

    // ── Environment settings writes (5b) ──────────────────────────────────

    [Fact]
    public async Task Setting_the_app_cadence_writes_to_bc_and_refreshes_our_cached_value()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SetAppUpdateCadenceAsync(projectId, envId, BcAppUpdateCadence.DuringMajorUpgrade);

        admin.Cadence.Should().Be(BcAppUpdateCadence.DuringMajorUpgrade);
        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.AppSourceAppsUpdateCadence.Should().Be(BcAppUpdateCadence.DuringMajorUpgrade,
            "the page must agree with the tenant straight after the write");
    }

    [Fact]
    public async Task An_unknown_cadence_never_reaches_business_central()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SetAppUpdateCadenceAsync(projectId, envId, "Whenever");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Cadence");
        admin.Cadence.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_setting_write_surfaces_readably()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            WriteThrows = new BcApiException(System.Net.HttpStatusCode.BadRequest,
                "Business Central no longer has this environment. Refresh the environments and try again."),
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SetM365AccessAsync(projectId, envId, true);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["M365Access"];
        error.Should().Contain("Refresh the environments");
    }

    [Fact]
    public async Task Selecting_a_target_version_refuses_one_business_central_has_not_released()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            // 27.7 exists but is not available yet - a stale page must not schedule it.
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, true, "scheduled", "GA", null, null, false, "Active", null, null),
                new BcEnvironmentUpdate("27.7", false, false, "", "GA", null, null, false, "", 12, 2026),
            },
        };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.7");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["TargetVersion"]
            .Should().Contain("isn't available");
        admin.SelectedVersion.Should().BeNull();
    }

    [Fact]
    public async Task Selecting_an_available_target_version_passes_its_type_through()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnEnvironmentUpdates = _ => new[]
            {
                new BcEnvironmentUpdate("27.6", true, false, "", "GA", null, null, false, "Active", null, null),
            },
        };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.6");

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedVersionType.Should().Be("GA", "preview versions are only valid for sandboxes, so the type travels with the choice");
    }

    // ── Version change over a stale date (issue #980) ─────────────────────

    /// <summary>
    /// The current selection (28.5) and the target (29.2) as one updates list. After the
    /// write the re-read shows 29.2 selected, so the re-mirror proof passes.
    /// </summary>
    private static FakeAdminClient AdminForVersionChange(
        DateTimeOffset? currentDate, DateTimeOffset? targetDate, DateTimeOffset? targetLatest)
    {
        var admin = new FakeAdminClient();
        admin.OnEnvironmentUpdates = _ => admin.SelectWrites == 0
            ? new[]
            {
                new BcEnvironmentUpdate("28.5", true, true, "scheduled", "GA", currentDate, null, false, "Active", null, null),
                new BcEnvironmentUpdate("29.2", true, false, "", "GA", targetDate, targetLatest, false, "Active", null, null),
            }
            : new[]
            {
                new BcEnvironmentUpdate("28.5", true, false, "", "GA", null, null, false, "Active", null, null),
                new BcEnvironmentUpdate("29.2", true, true, "scheduled", "GA", admin.SelectedDateTime, targetLatest, false, "Active", null, null),
            };
        return admin;
    }

    [Fact]
    public async Task Changing_the_version_sends_no_date_when_the_target_has_none()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var now = _clock.GetUtcNow();
        var admin = AdminForVersionChange(now.AddDays(10), targetDate: null, targetLatest: now.AddDays(40));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "29.2");

        admin.SelectedVersion.Should().Be("29.2");
        admin.SelectedDateTime.Should().BeNull("Business Central keeps or assigns the date when the target carries none");
        admin.SelectedIgnoreUpdateWindow.Should().BeNull();
    }

    [Fact]
    public async Task Changing_the_version_over_a_past_date_keeps_the_customers_current_slot()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var now = _clock.GetUtcNow();
        var agreed = now.AddDays(10);
        var admin = AdminForVersionChange(agreed, targetDate: now.AddDays(-2), targetLatest: now.AddDays(40));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "29.2");

        admin.SelectedVersion.Should().Be("29.2");
        admin.SelectedDateTime.Should().Be(agreed, "the agreed slot survives the version change when the new version allows it");
        admin.SelectedIgnoreUpdateWindow.Should().BeNull("only 'Start update' takes the customer's window away");
    }

    [Fact]
    public async Task Changing_the_version_over_a_past_date_sends_the_latest_allowed_when_the_current_slot_has_passed()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var now = _clock.GetUtcNow();
        var bound = new DateTimeOffset(now.UtcDateTime.Date.AddDays(40), TimeSpan.Zero);
        var admin = AdminForVersionChange(now.AddDays(-1), targetDate: now.AddDays(-2), targetLatest: bound);

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "29.2");

        admin.SelectedDateTime.Should().Be(BcUpdateSchedule.EffectiveLatest(bound),
            "a midnight bound is exclusive, so the last day allowed is the one before");
        admin.SelectedIgnoreUpdateWindow.Should().BeNull();
    }

    [Fact]
    public async Task Changing_the_version_over_a_past_date_with_no_bound_is_refused_and_sends_nothing()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        var now = _clock.GetUtcNow();
        var admin = AdminForVersionChange(now.AddDays(10), targetDate: now.AddDays(-2), targetLatest: null);

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "29.2");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["TargetVersion"]
            .Should().Contain("admin centre");
        admin.SelectWrites.Should().Be(0);
    }

    // ── Update-date writes (issue #657 Stage 3) ───────────────────────────

    private const int FlagUserId = 9600;
    private const int PlainTeamUserId = 9601;
    private const int OrgAdminUserId = 9602;

    private static readonly DateTimeOffset ScheduledDate = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    private static BcEnvironmentUpdate Update(
        DateTimeOffset? selectedDateTime, DateTimeOffset? latestSelectable, bool selected = true, bool ignoresWindow = false) =>
        new("27.6", true, selected, "scheduled", "GA", selectedDateTime, latestSelectable, ignoresWindow, "Active", null, null);

    private async Task SeedUserAsync(int id, string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x",
            DisplayName = email, Role = role, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Puts the update-flag holder and a plain colleague on one team and assigns it to the
    /// project — the only shape that grants the update-ops axis, since the flag counts
    /// only on a team the project is assigned to.
    /// </summary>
    private async Task SeedUpdateOpsTeamAsync(int projectId)
    {
        await SeedUserAsync(FlagUserId, "upgrade@example.com", UserRole.User);
        await SeedUserAsync(PlainTeamUserId, "colleague@example.com", UserRole.User);

        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "Upgrades", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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
            OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = PlainTeamUserId, CreatedAt = DateTime.UtcNow,
        });
        ctx.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// An updates list that answers the pre-write validation read one way and the
    /// re-mirror read another, so a test can prove the row is refreshed from Business
    /// Central rather than from what we asked for.
    /// </summary>
    private static FakeAdminClient AdminWithUpdates(BcEnvironmentUpdate before, BcEnvironmentUpdate after)
    {
        var admin = new FakeAdminClient();
        admin.OnEnvironmentUpdates = _ => new[] { admin.SelectWrites == 0 ? before : after };
        return admin;
    }

    [Fact]
    public async Task Pushing_the_date_sends_the_latest_selectable_date_and_remirrors_the_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedVersionType.Should().Be("GA");
        admin.SelectedDateTime.Should().Be(Latest);
        admin.SelectedIgnoreUpdateWindow.Should().BeNull("only 'update now' takes the customer's window away");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateDate.Should().Be(Latest.UtcDateTime, "the fleet page must show the new date without waiting for the sweep");
        row.BcNextUpdateVersion.Should().Be("27.6");
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Running_the_update_now_sends_today_and_ignores_the_window()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(
            Update(ScheduledDate, Latest),
            Update(DateTimeOffset.UtcNow, Latest, ignoresWindow: true));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        admin.SelectedVersion.Should().Be("27.6");
        admin.SelectedDateTime.Should().NotBeNull();
        admin.SelectedDateTime!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        admin.SelectedIgnoreUpdateWindow.Should().BeTrue("a customer who agreed a slot wants Microsoft to pick it up now");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateIgnoresWindow.Should().BeTrue();
        row.BcNextUpdateFetchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Pushing_the_date_records_an_audit_row_naming_the_action_and_the_environment()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        await using var verify = _db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking().SingleAsync();
        entry.EntityType.Should().Be(AuditEntityType.ProjectEnvironment);
        entry.EntityId.Should().Be(envId);
        entry.Action.Should().Be(AuditAction.Updated);
        entry.EntityName.Should().Be("Production");
        entry.ChangedByUserId.Should().Be(FlagUserId);
        entry.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        entry.ChangedBy.Should().Contain("upgrade@example.com",
            "the log names the person, and a circuit has no HttpContext for the interceptor to read");
        // The snapshot is the state before the write, plus the event in plain words -
        // the audit model records rows changing, and these two writes are events.
        entry.SnapshotJson.Should().NotBeNull();
        entry.SnapshotJson!.Should().Contain("Moved the update date out to the latest");
        entry.SnapshotJson.Should().Contain("27.6");
    }

    [Fact]
    public async Task Running_the_update_now_records_its_own_audit_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(
            Update(ScheduledDate, Latest),
            Update(DateTimeOffset.UtcNow, Latest, ignoresWindow: true));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        await using var verify = _db.NewContext();
        var entry = await verify.AuditLog.AsNoTracking().SingleAsync();
        entry.EntityId.Should().Be(envId);
        entry.SnapshotJson.Should().NotBeNull();
        entry.SnapshotJson!.Should().Contain("Started the update now",
            "the two fleet actions must be told apart in the log without opening the diff");
    }

    [Fact]
    public async Task A_refused_push_leaves_no_audit_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(0,
            "a skipped row changed nothing on the customer's tenant");
    }

    [Fact]
    public async Task Pushing_the_date_refuses_when_the_environment_has_nothing_on_offer()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("No update is available to reschedule.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Running_now_refuses_when_the_environment_has_nothing_on_offer()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).RunUpdateNowAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("No update is available to run.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_business_central_gave_no_last_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, null) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Contain("last possible date");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_already_at_the_latest_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(Latest, Latest) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("This update's date is already the latest Microsoft allows.");
        admin.SelectWrites.Should().Be(0, "a no-op must not touch the customer's tenant");
    }

    // ── The exclusive latest-selectable bound (issue #804) ────────────────

    /// <summary>Microsoft's bound as the reporter saw it: midnight UTC, meaning "before 1 March".</summary>
    private static readonly DateTimeOffset MidnightBound = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The last moment inside that bound, and what the PATCH must carry.</summary>
    private static readonly DateTimeOffset LastAllowedDay = new(2027, 2, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pushing_the_date_sends_the_day_before_an_exclusive_midnight_bound()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // Business Central stores the date inside the customer's update window, so the day
        // it reads back carries a time of day the PATCH never named.
        var landed = new DateTimeOffset(2027, 2, 28, 21, 0, 0, TimeSpan.Zero);
        var admin = AdminWithUpdates(Update(ScheduledDate, MidnightBound), Update(landed, MidnightBound));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(LastAllowedDay,
            "the bound is exclusive, so asking for it asks for a moment Business Central refuses");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateDate.Should().Be(landed.UtcDateTime,
            "the mirror says what Business Central stored, not what we asked for");
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_already_on_the_last_allowed_day()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // The same day as the effective latest, at a different time of day: an exact-tick
        // guard misses this and re-PATCHes the customer's tenant on every sweep.
        var onTheDay = new DateTimeOffset(2027, 2, 28, 21, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(onTheDay, MidnightBound) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("This update's date is already the latest Microsoft allows.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task A_date_that_lands_on_the_day_after_the_one_we_asked_for_still_counts_as_moved()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // The customer's update window opens at 02:00 in Copenhagen, which is 01:00 UTC on
        // the following day. The date moved, so the action is done - checking that it
        // landed on the exact day we sent would fail a write that worked.
        var landed = new DateTimeOffset(2027, 3, 1, 1, 0, 0, TimeSpan.Zero);
        var admin = AdminWithUpdates(Update(ScheduledDate, MidnightBound), Update(landed, MidnightBound));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(LastAllowedDay);

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(1);
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateDate.Should().Be(landed.UtcDateTime);
    }

    [Fact]
    public async Task Pushing_the_date_refuses_an_update_already_past_the_last_allowed_day()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // Where the window put it a day beyond the bound: as late as it goes, and
        // re-sending would fail loudly on every sweep.
        var pastTheBound = new DateTimeOffset(2027, 3, 1, 1, 0, 0, TimeSpan.Zero);
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(pastTheBound, MidnightBound) } };

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
            .Should().Be("This update's date is already the latest Microsoft allows.");
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task A_date_business_central_did_not_take_fails_the_push_instead_of_reporting_it_done()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // The PATCH reports no error and the re-read shows the very same date it showed
        // before - what issue #804 saw as a green "Done" entry on an environment that
        // never moved. Unchanged is what fails, whatever day we asked for.
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(ScheduledDate, Latest));

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
                .Should().Be($"Business Central did not accept the new date. Its schedule still says {ScheduledDate.UtcDateTime:yyyy-MM-dd}.");
        }

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(0,
            "nothing moved on the customer's tenant, so the history must not claim it did");
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.BcNextUpdateFetchedAt.Should().NotBeNull("the failed write still refreshed what we know");
    }

    [Fact]
    public async Task An_update_that_still_has_no_date_afterwards_fails_the_push_too()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        // Dateless before and dateless after is unchanged just as plainly as an old date
        // that stayed put, and must not be recorded as a move either.
        var admin = AdminWithUpdates(Update(null, Latest, selected: false), Update(null, Latest, selected: false));

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Update"]
                .Should().Be("Business Central did not accept the new date. Its schedule still has no date.");
        }

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_re_read_that_fails_outright_still_leaves_the_push_done()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient();
        admin.OnEnvironmentUpdates = _ => admin.SelectWrites == 0
            ? new[] { Update(ScheduledDate, Latest) }
            : throw new BcApiException(System.Net.HttpStatusCode.Forbidden, "denied");

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(Latest);

        await using var verify = _db.NewContext();
        (await verify.AuditLog.AsNoTracking().CountAsync()).Should().Be(1,
            "a re-read that fails costs the freshness, never the write");
    }

    [Fact]
    public async Task An_update_the_customer_has_not_picked_yet_is_selected_by_the_date_write()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminWithUpdates(Update(null, Latest, selected: false), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(Latest);
        admin.SelectWrites.Should().Be(1, "the same PATCH both picks the version and dates it");
    }

    // ── Re-reading one environment while its update runs (#982) ───────────

    private static BcEnvironmentUpdate RunningUpdate(string version = "27.6", string status = "Running") =>
        new(version, true, true, status, "GA", DateTimeOffset.UtcNow.AddMinutes(-6), Latest, true, "Active", null, null);

    private static FakeAdminClient AdminReporting(string status, string version, BcEnvironmentUpdate? update) => new()
    {
        OnGetEnvironment = name => new BcEnvironment(name, "Production")
        {
            ApplicationFamily = "BusinessCentral",
            Status = status,
            Version = version,
        },
        OnEnvironmentUpdates = _ => update is null ? Array.Empty<BcEnvironmentUpdate>() : new[] { update },
    };

    /// <summary>
    /// The watch on the Upgrades page calls this every ten seconds, so what it costs the
    /// customer's tenant is the point: the environment and its updates, nothing else, and
    /// only the one row written - a sibling environment of the same customer is not
    /// touched, which is what separates this from a Refresh.
    /// </summary>
    [Fact]
    public async Task Refreshing_one_environment_makes_two_reads_and_writes_only_its_row()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        DateTime siblingFetched;
        int siblingId;
        await using (var seed = _db.NewContext())
        {
            siblingFetched = DateTime.UtcNow.AddDays(-2);
            var sibling = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Sandbox", Type = "Sandbox",
                ApplicationFamily = "BusinessCentral", Status = "Active", Version = "27.4.1.0", FetchedAt = siblingFetched,
            };
            seed.OeProjectEnvironments.Add(sibling);
            await seed.SaveChangesAsync();
            siblingId = sibling.Id;
        }
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminReporting("Upgrading", "27.5.12345.0", RunningUpdate());

        BcEnvironmentReading reading;
        await using (var ctx = _db.NewContext())
            reading = await Svc(ctx, TokenOk(), admin).RefreshEnvironmentAsync(projectId, envId);

        admin.Reads.Should().Equal("GetEnvironment:Production", "ListEnvironmentUpdates:Production");

        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.Status.Should().Be("Upgrading");
        row.Version.Should().Be("27.5.12345.0");
        row.StatusFetchedAt.Should().NotBeNull();
        row.FetchedAt.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(1));
        row.BcNextUpdateVersion.Should().Be("27.6");
        row.BcNextUpdateStatus.Should().Be("Running");
        row.BcNextUpdateIgnoresWindow.Should().BeTrue();
        row.BcNextUpdateFetchedAt.Should().NotBeNull();

        var sibling2 = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == siblingId);
        sibling2.FetchedAt.Should().BeCloseTo(siblingFetched, TimeSpan.FromMilliseconds(1));
        sibling2.BcNextUpdateFetchedAt.Should().BeNull();

        // What the page lays over its row without reading the fleet again.
        reading.Status.Should().Be("Upgrading");
        reading.Version.Should().Be("27.5.12345.0");
        reading.NextUpdateVersion.Should().Be("27.6");
        reading.NextUpdateStatus.Should().Be("Running");
        // Postgres keeps microseconds and .NET keeps 100 ns ticks, so the stored stamp and
        // the one handed back can differ below a microsecond.
        reading.NextUpdateFetchedAt.Should().BeCloseTo(row.BcNextUpdateFetchedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Refreshing_one_environment_drops_the_panel_it_had_cached()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        _panelCache.Set(projectId, envId, new BcEnvironmentPanel(
            "Production", new HashSet<Guid>(), [], null, [], null, [], null, [], null, DateTime.UtcNow));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), AdminReporting("Active", "27.6.1.0", null)).RefreshEnvironmentAsync(projectId, envId);

        _panelCache.Get(projectId, envId).Should().BeNull("the panel must not show an answer older than the row");
    }

    [Fact]
    public async Task Refreshing_one_environment_needs_the_environment_updates_grant()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = PlainTeamUserId;
        var admin = AdminReporting("Upgrading", "27.5.12345.0", RunningUpdate());

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).RefreshEnvironmentAsync(projectId, envId);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        }

        admin.Reads.Should().BeEmpty("a person who may not act on the solution's updates does not get to read its tenant");
        await using var verify = _db.NewContext();
        (await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId)).Status.Should().BeNull();
    }

    [Fact]
    public async Task Refreshing_an_environment_business_central_no_longer_has_writes_nothing()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnGetEnvironment = _ => null };

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).RefreshEnvironmentAsync(projectId, envId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors
                .Should().ContainKey("Environment", "a caller watching it must stop rather than ask again");
        }

        admin.Reads.Should().Equal("GetEnvironment:Production");
        await using var verify = _db.NewContext();
        (await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId)).BcNextUpdateFetchedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_updates_read_leaves_the_row_as_it_was()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = AdminReporting("Active", "27.6.1.0", null);
        admin.OnEnvironmentUpdates = _ => throw new BcApiException(HttpStatusCode.ServiceUnavailable, "busy");

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk(), admin).RefreshEnvironmentAsync(projectId, envId);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors
                .Should().ContainKey("Refresh", "a read that failed is worth asking again");
        }

        // A state without its update would let the page call a running update over.
        await using var verify = _db.NewContext();
        var row = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == envId);
        row.Status.Should().BeNull();
        row.BcNextUpdateFetchedAt.Should().BeNull();
    }

    // ── The two axes: manage and environment updates ──────────────────────

    [Fact]
    public async Task A_plain_member_of_the_projects_team_cannot_move_update_dates()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = PlainTeamUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, Latest) } };

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        await ((Func<Task>)(() => svc.PushUpdateDateToLatestAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.RunUpdateNowAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        admin.SelectWrites.Should().Be(0);
    }

    [Fact]
    public async Task The_projects_owner_can_pick_the_version_but_not_move_its_date()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        // The owner manages the project and holds no update flag: the two axes apart.
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(ScheduledDate, Latest));

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk(), admin);

        await ((Func<Task>)(() => svc.PushUpdateDateToLatestAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.RunUpdateNowAsync(projectId, envId)))
            .Should().ThrowAsync<ProjectAccessDeniedException>();

        await svc.SelectTargetVersionAsync(projectId, envId, "27.6");
        admin.SelectedVersion.Should().Be("27.6", "picking the version stays open to whoever manages the project");
    }

    [Fact]
    public async Task The_update_flag_holder_can_pick_the_version_too()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUpdateOpsTeamAsync(projectId);
        _db.OrgContext.CurrentUserId = FlagUserId;
        var admin = new FakeAdminClient { OnEnvironmentUpdates = _ => new[] { Update(ScheduledDate, Latest) } };

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).SelectTargetVersionAsync(projectId, envId, "27.6");

        admin.SelectedVersion.Should().Be("27.6");
    }

    [Fact]
    public async Task An_org_admin_can_move_update_dates_anywhere_in_the_organisation()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(OrgAdminUserId, "ada@example.com", UserRole.Admin);
        _db.OrgContext.CurrentUserId = OrgAdminUserId;
        var admin = AdminWithUpdates(Update(ScheduledDate, Latest), Update(Latest, Latest));

        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk(), admin).PushUpdateDateToLatestAsync(projectId, envId);

        admin.SelectedDateTime.Should().Be(Latest);
    }

    [Fact]
    public async Task Picking_the_version_names_both_ways_in_when_it_is_refused()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();
        await SeedUserAsync(9604, "outsider@example.com", UserRole.User);
        await NarrowAsync(projectId);
        _db.OrgContext.CurrentUserId = 9604;

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk()).SelectTargetVersionAsync(projectId, envId, "27.6");

        (await act.Should().ThrowAsync<ProjectAccessDeniedException>())
            .Which.Message.Should().Contain("environment updates");
    }

    // ── The audit scope (fence-adjacent: see AuditInterceptor) ────────────

    [Fact]
    public async Task A_cadence_change_is_audited()
    {
        var (projectId, envId) = await SeedEnvironmentAsync();

        // The audit interceptor is only attached on a context that asks for it, so both
        // halves of this pair have to use one or the assertion proves nothing.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            await Svc(ctx, TokenOk(), new FakeAdminClient()).SetAppUpdateCadenceAsync(projectId, envId, BcAppUpdateCadence.DuringMajorUpgrade);

        await using var verify = _db.NewContext();
        var rows = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment && a.EntityId == envId)
            .ToListAsync();
        rows.Should().ContainSingle("a deliberate change to the customer's tenant belongs in the trail");
    }

    [Fact]
    public async Task Refreshing_the_environments_writes_no_audit_rows()
    {
        // The whole reason the audit scope is column-scoped: a Refresh rewrites status,
        // version, family and the mirrored BC window on every environment. Auditing that
        // would bury the changes that matter under one row per environment per click.
        var (projectId, _) = await SeedEnvironmentAsync();
        var admin = new FakeAdminClient
        {
            OnList = () => new[] { new BcEnvironment("Production", "Production") { Status = "Active", Version = "27.5.5.15" } },
            OnUpdateSettings = _ => new BcUpdateSettings(new TimeOnly(2, 0), new TimeOnly(6, 0), "Romance Standard Time"),
        };

        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
            await Svc(ctx, TokenOk(), admin).RefreshEnvironmentsAsync(projectId);

        await using var verify = _db.NewContext();
        var rows = await verify.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType.ProjectEnvironment)
            .ToListAsync();
        rows.Should().BeEmpty("fetched cache is not an edit");
    }

    // ── Access control ────────────────────────────────────────────────────

    [Fact]
    public async Task Mutations_are_blocked_for_a_non_owner_non_admin()
    {
        var id = await SeedProjectAsync();
        // Read-only: a Public solution is managed by everyone in the organisation, so
        // there is no stranger to it. The customer's credentials are what this guards.
        await NarrowAsync(id);

        const int strangerId = 9500;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = strangerId, OrganizationId = TestDb.DefaultOrgId, Email = "stranger@example.com",
                PasswordHash = "x", DisplayName = "Stranger", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentUserId = strangerId;
        try
        {
            await using var ctx = _db.NewContext();
            var svc = Svc(ctx, TokenOk());

            await ((Func<Task>)(() => svc.SaveConnectionAsync(id, ValidConnection())))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => svc.TestConnectionAsync(id)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    // ── The organisation's own app registration ───────────────────────────

    /// <summary>Records the client id each token request signs in with.</summary>
    private sealed class CapturingTokenHandler : HttpMessageHandler
    {
        public List<string> ClientIds { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            ClientIds.Add(System.Web.HttpUtility.ParseQueryString(form)["client_id"] ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}") };
        }
    }

    private const string OrgClientId = "22222222-2222-2222-2222-222222222222";

    private async Task SaveOrgRegistrationAsAdminAsync(DateTime? expires = null)
    {
        await using (var users = _db.NewContext())
        {
            if (!await users.Users.AnyAsync(u => u.Id == 9790))
            {
                await SeedUserAsync(9790, "registration-admin@example.com", UserRole.Admin);
            }
        }
        var previous = _db.OrgContext.CurrentUserId;
        _db.OrgContext.CurrentUserId = 9790;
        try
        {
            await using var ctx = _db.NewContext();
            await Svc(ctx, TokenOk()).SaveOrganizationRegistrationAsync(
                new OrganizationBcRegistrationInput(OrgClientId.ToUpperInvariant(), "org-secret", expires ?? DateTime.UtcNow.AddYears(1)));
        }
        finally
        {
            _db.OrgContext.CurrentUserId = previous;
        }
    }

    [Fact]
    public async Task A_solution_with_only_a_tenant_connects_with_the_organisations_registration()
    {
        var id = await SeedProjectAsync();
        await SaveOrgRegistrationAsAdminAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), null, null, null, null, UseOrganizationRegistration: true));
        }

        var handler = new CapturingTokenHandler();
        var tokens = new BcTokenService(new StubFactory(handler), NullLogger<BcTokenService>.Instance);
        await using (var ctx = _db.NewContext())
        {
            var result = await Svc(ctx, tokens).TestConnectionAsync(id);
            result.Result.Should().Be(BcConnectionResult.Success);
        }

        handler.ClientIds.Should().Equal(OrgClientId);
        await using var read = _db.NewContext();
        var status = await Svc(read, TokenOk()).GetConnectionAsync(id);
        status!.IsConfigured.Should().BeTrue();
        status.UsesOrganizationRegistration.Should().BeTrue();
        status.OrganizationClientId.Should().Be(OrgClientId);
    }

    [Fact]
    public async Task A_solution_with_its_own_registration_keeps_it_and_switching_clears_it()
    {
        var id = await SeedProjectAsync();
        await SaveOrgRegistrationAsAdminAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id, ValidConnection());

        var handler = new CapturingTokenHandler();
        var tokens = new BcTokenService(new StubFactory(handler), NullLogger<BcTokenService>.Instance);
        await using (var ctx = _db.NewContext())
            await Svc(ctx, tokens).TestConnectionAsync(id);
        handler.ClientIds.Should().Equal("client-abc");

        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), "ignored", "ignored", null, null, UseOrganizationRegistration: true));
        }
        await using var read = _db.NewContext();
        var row = await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == id);
        row.BcClientId.Should().BeNull();
        row.BcClientSecretEncrypted.Should().BeNull("a stored secret nobody uses is one more thing to leak");
        row.BcClientSecretExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Choosing_the_organisations_registration_is_refused_when_there_is_none()
    {
        var id = await SeedProjectAsync();

        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx, TokenOk()).SaveConnectionAsync(id,
            new BcConnectionInput(Guid.NewGuid(), null, null, null, null, UseOrganizationRegistration: true));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["BcClientId"]
            .Should().Contain("Administration");
    }

    [Fact]
    public async Task An_expired_own_secret_is_refused_by_name_and_never_falls_back_to_the_organisations()
    {
        var id = await SeedProjectAsync();
        await SaveOrgRegistrationAsAdminAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), "client-abc", "own-secret", DateTime.UtcNow.AddDays(-1), null));
        }

        var handler = new CapturingTokenHandler();
        var tokens = new BcTokenService(new StubFactory(handler), NullLogger<BcTokenService>.Instance);
        await using var act = _db.NewContext();
        var acquire = () => Svc(act, tokens).AcquireDeliveryContextAsync(id);

        (await acquire.Should().ThrowAsync<BcApiException>()).Which.Message
            .Should().Contain("This solution's own").And.Contain("switch the solution");
        handler.ClientIds.Should().BeEmpty("nothing may sign in as a registration nobody chose for this customer");
    }

    [Fact]
    public async Task An_expired_organisation_secret_sends_the_person_to_an_administrator()
    {
        var id = await SeedProjectAsync();
        await SaveOrgRegistrationAsAdminAsync(expires: DateTime.UtcNow.AddDays(-1));
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(id,
                new BcConnectionInput(Guid.NewGuid(), null, null, null, null, UseOrganizationRegistration: true));
        }

        await using var act = _db.NewContext();
        var acquire = () => Svc(act, TokenOk()).AcquireDeliveryContextAsync(id);

        (await acquire.Should().ThrowAsync<BcApiException>()).Which.Message.Should().Contain("administrator");
    }

    [Fact]
    public async Task Only_an_administrator_reads_or_changes_the_organisations_registration()
    {
        // The fixture's owner is an Editor: enough to own a solution, not enough for this.
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx, TokenOk());

        await ((Func<Task>)(() => svc.GetOrganizationRegistrationAsync())).Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.SaveOrganizationRegistrationAsync(
            new OrganizationBcRegistrationInput(OrgClientId, "s", DateTime.UtcNow.AddYears(1))))).Should().ThrowAsync<ProjectAccessDeniedException>();
        await ((Func<Task>)(() => svc.ClearOrganizationRegistrationAsync())).Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task Saving_the_organisations_registration_encrypts_the_secret_and_unverifies_the_solutions_on_it()
    {
        var onOrg = await SeedProjectAsync();
        var onOwn = await SeedProjectAsync("CRONUS International Ltd.");
        await SaveOrgRegistrationAsAdminAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx, TokenOk()).SaveConnectionAsync(onOrg,
                new BcConnectionInput(Guid.NewGuid(), null, null, null, null, UseOrganizationRegistration: true));
            await Svc(ctx, TokenOk()).SaveConnectionAsync(onOwn, ValidConnection());
            await Svc(ctx, TokenOk()).TestConnectionAsync(onOrg);
            await Svc(ctx, TokenOk()).TestConnectionAsync(onOwn);
        }

        await SaveOrgRegistrationAsAdminAsync();

        await using var read = _db.NewContext();
        var settings = await read.OrganizationSettings.AsNoTracking().SingleAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
        settings.BcClientId.Should().Be(OrgClientId, "stored lowercased, whatever was typed");
        settings.BcClientSecretEncrypted.Should().NotBeNullOrEmpty().And.NotContain("org-secret");
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == onOrg)).BcConnectionVerifiedAt
            .Should().BeNull("the last successful test was of the old credentials");
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == onOwn)).BcConnectionVerifiedAt
            .Should().NotBeNull("a solution on its own registration is not touched");
    }

    [Theory]
    [InlineData("not-a-guid", "secret", true, "BcClientId")]
    [InlineData(OrgClientId, null, true, "BcClientSecret")]
    [InlineData(OrgClientId, "secret", false, "BcClientSecretExpiresAt")]
    public async Task The_organisations_registration_says_which_field_is_wrong(string clientId, string? secret, bool withExpiry, string field)
    {
        await SeedUserAsync(9791, $"admin-{Guid.NewGuid():N}@example.com", UserRole.Admin);
        _db.OrgContext.CurrentUserId = 9791;
        try
        {
            await using var ctx = _db.NewContext();
            var act = () => Svc(ctx, TokenOk()).SaveOrganizationRegistrationAsync(
                new OrganizationBcRegistrationInput(clientId, secret, withExpiry ? DateTime.UtcNow.AddYears(1) : null));

            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey(field);
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    // ── Delivery window on many environments at once (#961) ───────────────

    private static readonly TimeOnly Ten = new(22, 0);
    private static readonly TimeOnly Six = new(6, 0);

    /// <summary>
    /// A fleet with one of each: a row that changes, a row already at the target, a
    /// deleted one, and - on a Read-only solution someone else owns - one the owner
    /// cannot manage.
    /// </summary>
    private async Task<(int Changes, int AlreadySet, int Deleted, int Foreign, int ForeignProject)> SeedWindowFleetAsync()
    {
        var mine = await SeedProjectAsync();
        await SeedUserAsync(9801, "someone-else@example.com", UserRole.Editor);
        var theirs = await SeedProjectAsync("Fabrikam");
        await using var ctx = _db.NewContext();
        (await ctx.OeProjects.SingleAsync(p => p.Id == theirs)).CreatedByUserId = 9801;
        await ctx.SaveChangesAsync();
        await NarrowAsync(theirs);

        OeProjectEnvironment Env(int projectId, string name, TimeOnly? start = null, TimeOnly? end = null) => new()
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = "Production",
            ApplicationFamily = "BusinessCentral", FetchedAt = DateTime.UtcNow,
            UpdateWindowStart = start, UpdateWindowEnd = end,
        };
        var changes = Env(mine, "Production");
        var already = Env(mine, "Sandbox", Ten, Six);
        var deleted = Env(mine, "Old", new TimeOnly(1, 0), new TimeOnly(2, 0));
        deleted.SoftDeletedOn = DateTime.UtcNow.AddDays(-1);
        var foreign = Env(theirs, "Production");
        ctx.OeProjectEnvironments.AddRange(changes, already, deleted, foreign);
        await ctx.SaveChangesAsync();
        return (changes.Id, already.Id, deleted.Id, foreign.Id, theirs);
    }

    [Fact]
    public async Task The_bulk_window_preview_groups_each_selected_environment()
    {
        var fleet = await SeedWindowFleetAsync();
        const int neverExisted = 987654;

        await using var ctx = _db.NewContext();
        var preview = await Svc(ctx, TokenOk()).PreviewUpdateWindowForManyAsync(
            [fleet.Changes, fleet.AlreadySet, fleet.Deleted, fleet.Foreign, neverExisted], Ten, Six);

        preview.Select(p => (p.EnvironmentId, p.Group)).Should().Equal(
            (fleet.Changes, DeliveryWindowChangeGroup.WillChange),
            (fleet.AlreadySet, DeliveryWindowChangeGroup.AlreadySet),
            (fleet.Deleted, DeliveryWindowChangeGroup.Missing),
            (fleet.Foreign, DeliveryWindowChangeGroup.NoAccess),
            (neverExisted, DeliveryWindowChangeGroup.Missing));
        var changing = preview[0];
        changing.EnvironmentName.Should().Be("Production");
        changing.ProjectName.Should().Be("CRONUS A/S");
        changing.CurrentStart.Should().BeNull("it has no window yet, which the dialog words as Any time");
        preview[4].EnvironmentName.Should().BeNull("an id nobody can read has no name to give");
    }

    [Fact]
    public async Task The_bulk_window_preview_says_when_a_deployment_waits_for_the_current_window()
    {
        var fleet = await SeedWindowFleetAsync();
        await using (var seed = _db.NewContext())
        {
            var env = await seed.OeProjectEnvironments.SingleAsync(e => e.Id == fleet.Changes);
            var pipeline = new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = env.ProjectId, Name = "Build",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OePipelines.Add(pipeline);
            await seed.SaveChangesAsync();
            var build = new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = env.ProjectId, PipelineId = pipeline.Id,
                Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
            };
            seed.OeProjectBuilds.Add(build);
            var release = new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = env.ProjectId, Name = "To production",
                BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleasePipelines.Add(release);
            await seed.SaveChangesAsync();
            seed.OeProjectDeliveries.Add(new OeProjectDelivery
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = env.ProjectId,
                ReleasePipelineId = release.Id, ProjectBuildId = build.Id, EnvironmentName = env.Name,
                ScheduledFor = DateTime.UtcNow.AddHours(8), ScheduledByDeliveryWindow = true,
                Status = ProjectDeliveryStatus.Scheduled, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var preview = await Svc(ctx, TokenOk()).PreviewUpdateWindowForManyAsync([fleet.Changes, fleet.AlreadySet], Ten, Six);

        preview.Single(p => p.EnvironmentId == fleet.Changes).HasDeploymentWaitingForWindow.Should().BeTrue();
        preview.Single(p => p.EnvironmentId == fleet.AlreadySet).HasDeploymentWaitingForWindow.Should().BeFalse();
    }

    [Fact]
    public async Task Setting_a_window_on_many_writes_only_the_rows_that_change_and_reports_each()
    {
        var fleet = await SeedWindowFleetAsync();
        const int neverExisted = 987655;

        List<DeliveryWindowChangeResult> results;
        await using (var ctx = _db.NewContext())
        {
            results = await Svc(ctx, TokenOk()).SetUpdateWindowForManyAsync(
                [fleet.Changes, fleet.AlreadySet, fleet.Deleted, fleet.Foreign, neverExisted], Ten, Six);
        }

        results.Select(r => (r.Row.EnvironmentId, r.Outcome)).Should().Equal(
            (fleet.Changes, DeliveryWindowChangeOutcome.Changed),
            (fleet.AlreadySet, DeliveryWindowChangeOutcome.Skipped),
            (fleet.Deleted, DeliveryWindowChangeOutcome.Skipped),
            (fleet.Foreign, DeliveryWindowChangeOutcome.Skipped),
            (neverExisted, DeliveryWindowChangeOutcome.Skipped));

        await using var verify = _db.NewContext();
        var rows = await verify.OeProjectEnvironments.AsNoTracking()
            .Where(e => new[] { fleet.Changes, fleet.Deleted, fleet.Foreign }.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);
        (rows[fleet.Changes].UpdateWindowStart, rows[fleet.Changes].UpdateWindowEnd).Should().Be((Ten, Six));
        (rows[fleet.Deleted].UpdateWindowStart, rows[fleet.Deleted].UpdateWindowEnd)
            .Should().Be((new TimeOnly(1, 0), new TimeOnly(2, 0)), "a deleted environment is skipped");
        rows[fleet.Foreign].UpdateWindowStart.Should().BeNull("the owner cannot manage somebody else's read-only solution");
    }

    [Fact]
    public async Task Any_time_on_many_clears_the_window()
    {
        var fleet = await SeedWindowFleetAsync();

        await using (var ctx = _db.NewContext())
        {
            var results = await Svc(ctx, TokenOk()).SetUpdateWindowForManyAsync([fleet.Changes, fleet.AlreadySet], null, null);
            results.Select(r => r.Outcome).Should().Equal(DeliveryWindowChangeOutcome.Skipped, DeliveryWindowChangeOutcome.Changed);
        }

        await using var verify = _db.NewContext();
        var env = await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == fleet.AlreadySet);
        env.UpdateWindowStart.Should().BeNull();
        env.UpdateWindowEnd.Should().BeNull();
    }

    [Fact]
    public async Task A_bulk_window_with_only_one_end_is_refused_before_anything_is_written()
    {
        var fleet = await SeedWindowFleetAsync();

        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx, TokenOk()).SetUpdateWindowForManyAsync([fleet.Changes], Ten, null);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UpdateWindow");
        }

        await using var verify = _db.NewContext();
        (await verify.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == fleet.Changes))
            .UpdateWindowStart.Should().BeNull();
    }

    [Fact]
    public async Task The_short_name_rides_on_the_fleet_row()
    {
        var (projectId, _) = await SeedEnvironmentAsync();
        await using (var seed = _db.NewContext())
        {
            (await seed.OeProjects.SingleAsync(p => p.Id == projectId)).ShortName = "CRN";
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var fleet = new UpgradeFleetService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new EnvironmentRefreshQueue(), NullLogger<UpgradeFleetService>.Instance);
        (await fleet.ListFleetAsync()).Single().ProjectShortName.Should().Be("CRN");
    }
}
