using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Cookbook;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Per-test Postgres database scaffolded on a process-wide shared host
/// (Milestone P4.16). The host is either:
///   * a runner-provided service container — when the
///     <c>ALDT_TEST_POSTGRES_CONNECTION</c> env var is set, that connection
///     string is used as-is. CI uses this path against
///     <c>postgres:18</c> as a service container; or
///   * a Testcontainers <c>postgres:18-alpine</c> spun up on first use —
///     local-dev path. Requires Docker on the host.
///
/// Each test fixture gets a unique database off the shared host and drops it
/// on dispose. The schema is NOT migrated per fixture: it is built once per
/// test process into a template database, and every fixture is a
/// <c>CREATE DATABASE ... TEMPLATE</c> clone of it, which Postgres serves as a
/// file copy.
///
/// <para>That indirection is the difference between a usable test suite and an
/// unusable one. xUnit constructs a new instance of a test class for every test
/// method, and ~164 classes hold this type as a field initialiser, so the old
/// per-fixture <c>Migrate()</c> ran all 116 migrations roughly 3,200 times per
/// run: 4.4 s per test measured, and a 34-minute CI test step. Cloning the
/// template measured 165-260 ms against the same 81-table schema. See issue
/// #728 for the numbers.</para>
/// </summary>
public sealed class TestDb : IDisposable
{
    public const int DefaultOrgId = 1;
    public const int OtherOrgId = 2;

    private static readonly Lazy<PostgresHost> SharedHost = new(PostgresHost.Start, isThreadSafe: true);

    /// <summary>
    /// The migrated, seeded database every fixture is cloned from. Built on
    /// first use and shared by the whole test process (#728).
    /// </summary>
    private static readonly Lazy<string> SharedTemplate = new(BuildTemplate, isThreadSafe: true);

    private readonly string _databaseName;
    private readonly string _connectionString;
    private readonly DbContextOptions<AppDbContext> _options;

    /// <summary>
    /// Connection string to the per-fixture database. Exposed for tests that
    /// boot the real app pipeline (e.g. via <c>WebApplicationFactory&lt;Program&gt;</c>)
    /// and need to point startup at the same scratch database the fixture set up.
    /// </summary>
    public string ConnectionString => _connectionString;

    public AmbientOrganizationContext OrgContext { get; } = new() { CurrentOrganizationId = DefaultOrgId };

    /// <summary>
    /// Counts EF commands still holding a connection on this fixture's
    /// database. Component tests register it on the DbContext they hand to
    /// bUnit and call <see cref="WaitForQueriesToSettle"/> before disposing
    /// bUnit's service provider - see <see cref="InFlightCommandTracker"/> for
    /// why that ordering matters.
    /// </summary>
    public InFlightCommandTracker CommandTracker { get; } = new();

    /// <summary>
    /// Waits for component-initiated queries to finish before teardown pulls
    /// the DbContext out from under them. Bounded; returns whether it settled
    /// so a caller could assert on it, though none needs to today.
    /// </summary>
    public bool WaitForQueriesToSettle() =>
        CommandTracker.WaitUntilIdle(TimeSpan.FromSeconds(10));

    public TestDb()
    {
        var host = SharedHost.Value;
        _databaseName = "aldt_test_" + Guid.NewGuid().ToString("N");
        host.CloneDatabase(_databaseName, SharedTemplate.Value);
        _connectionString = host.ConnectionStringFor(_databaseName);

        _options = BuildOptions(_connectionString);
        var options = _options;
        var orgContext = OrgContext;
        _scopeProvider = new Lazy<ServiceProvider>(() => new ServiceCollection()
            .AddScoped(_ => new AppDbContext(options, orgContext))
            .BuildServiceProvider());
        _openIddict = new Lazy<ServiceProvider>(() => new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => new AppDbContext(options, orgContext))
            .AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .Services
            .BuildServiceProvider());
    }

    private readonly Lazy<ServiceProvider> _openIddict;

    /// <summary>
    /// An OpenIddict token manager over this fixture's database, for services
    /// that revoke a user's OAuth tokens (<see cref="ALDevToolbox.Services.Account.UserAdministrationService"/>).
    /// </summary>
    public OpenIddict.Abstractions.IOpenIddictTokenManager OpenIddictTokens =>
        _openIddict.Value.GetRequiredService<OpenIddict.Abstractions.IOpenIddictTokenManager>();

    /// <summary>Org-admin actions on user accounts, wired to this fixture's clock and token manager.</summary>
    public ALDevToolbox.Services.Account.UserAdministrationService NewUserAdministrationService(AppDbContext ctx, TimeProvider clock) =>
        new(ctx, clock, OpenIddictTokens);

    /// <summary>
    /// Builds the process-wide template database: migrate once, seed once. Every
    /// fixture is a clone of the result, so anything added here is visible to
    /// every test and must stay as neutral as the migrated schema itself.
    /// </summary>
    private static string BuildTemplate()
    {
        var host = SharedHost.Value;
        var name = "aldt_tmpl_" + Guid.NewGuid().ToString("N");
        host.CreateDatabase(name);

        // Pooling off for the build: Postgres refuses to clone a template while
        // any session is connected to it, and a pooled connection outlives the
        // DbContext that opened it. Npgsql would hand the clone a 55006 for
        // every fixture in the run.
        var connectionString = new NpgsqlConnectionStringBuilder(host.ConnectionStringFor(name))
        {
            Pooling = false,
        }.ConnectionString;

        var orgContext = new AmbientOrganizationContext { CurrentOrganizationId = DefaultOrgId };
        using (var ctx = new AppDbContext(BuildOptions(connectionString), orgContext))
        {
            ctx.Database.Migrate();

            // Migration seeds the Default organisation and stamps it as the
            // singleton system org. Add the Other organisation tests use to
            // verify cross-org isolation; it stays a regular org.
            ctx.Organizations.Add(new Organization
            {
                Id = OtherOrgId,
                Name = "Other",
                Slug = "other",
                CreatedAt = DateTime.UtcNow,
            });
            ctx.SaveChanges();

            // Postgres identity sequences don't advance when a row is inserted
            // with an explicit id; the next nextval() would otherwise collide
            // with our seeded OtherOrg at id=2. Re-align the sequence to MAX(id)
            // so SignupAsync-style inserts in tests get a free id.
            ctx.Database.ExecuteSqlRaw(
                "SELECT setval(pg_get_serial_sequence('organizations', 'id'), (SELECT MAX(id) FROM organizations))");
        }

        // The template outlives every fixture, so drop it when the process ends.
        // On CI and on the Testcontainers path the whole server is thrown away
        // anyway; this only matters for a developer pointing
        // ALDT_TEST_POSTGRES_CONNECTION at a Postgres they keep around.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { host.DropDatabase(name); }
            catch (Exception) { /* best effort: the run is already over */ }
        };

        return name;
    }

    private static DbContextOptions<AppDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    /// <summary>Returns a fresh context bound to the per-fixture database, scoped to <see cref="OrgContext"/>.</summary>
    public AppDbContext NewContext() => new(_options, OrgContext);

    /// <summary>
    /// Returns a factory handing out fresh contexts bound to the per-fixture
    /// database, for the services that open a context per call rather than
    /// holding a scoped one (<see cref="AuditService"/>; see issue #741).
    /// </summary>
    public IDbContextFactory<AppDbContext> NewContextFactory() => new TestContextFactory(this);

    private sealed class TestContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly TestDb _db;

        public TestContextFactory(TestDb db) => _db = db;

        public AppDbContext CreateDbContext() => _db.NewContext();
    }

    /// <summary>
    /// Returns a fresh context with the audit interceptor wired up. Lets audit
    /// tests exercise the same write-path the application uses without going
    /// through DI.
    /// </summary>
    public AppDbContext NewContextWithAudit(AuditInterceptor interceptor) => NewContext(interceptor);

    /// <summary>
    /// Returns a fresh context with <paramref name="interceptor"/> wired up.
    /// Beyond auditing, this is how a test makes something happen *during* a
    /// save - a <c>SaveChangesInterceptor</c> that writes through a second
    /// context is the deterministic way to reproduce a race between a service's
    /// pre-check and its save.
    /// </summary>
    public AppDbContext NewContext(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new AppDbContext(options, OrgContext);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        // Before ClearAllPools: this provider owns scoped AppDbContexts, and a
        // live one would hand a connection straight back to the pool after the
        // clear and block the drop.
        if (_scopeProvider.IsValueCreated) _scopeProvider.Value.Dispose();
        if (_openIddict.IsValueCreated) _openIddict.Value.Dispose();
        // Idle pool connections hold open the per-fixture database and would
        // block DROP DATABASE; clear them before issuing the drop.
        NpgsqlConnection.ClearAllPools();
        SharedHost.Value.DropDatabase(_databaseName);
    }

    private readonly MemoryCache _memoryCache = new(Options.Create(new MemoryCacheOptions()));

    /// <summary>
    /// Returns a fresh <see cref="OrganizationConfigService"/> for tests that
    /// don't need the live DI graph. The on-disk seed has been retired; the
    /// service no longer touches the filesystem.
    ///
    /// Hands the service a per-fixture <see cref="IMemoryCache"/> so parallel
    /// xUnit fixtures — each on their own per-fixture database but inside
    /// the same process — can't race on a shared cache slot. See issue #45
    /// for the failure mode this isolation prevents.
    /// </summary>
    public OrganizationConfigService NewOrganizationConfigService(AppDbContext ctx) =>
        new(ctx, OrgContext, NewQuotaGuard(ctx), NullLogger<OrganizationConfigService>.Instance,
            _memoryCache, ScopeFactory);

    /// <summary>
    /// The organisation's name and logo, split out of
    /// <see cref="OrganizationConfigService"/>. Shares this fixture's config
    /// service (and therefore its cache) so a rename or a logo upload here is
    /// visible to the next read, as it is in the app.
    /// </summary>
    public OrganizationBrandingService NewOrganizationBrandingService(AppDbContext ctx) =>
        new(ctx, OrgContext, NewQuotaGuard(ctx), NewOrganizationConfigService(ctx),
            NullLogger<OrganizationBrandingService>.Instance);

    /// <summary>
    /// The per-org repository-provider allow-list, split out of
    /// <see cref="OrganizationConfigService"/> and sharing its cache.
    /// </summary>
    public RepositoryProviderPolicyService NewRepositoryProviderPolicyService(AppDbContext ctx) =>
        new(ctx, OrgContext, NewOrganizationConfigService(ctx),
            NullLogger<RepositoryProviderPolicyService>.Instance);

    /// <summary>
    /// The wipe-and-replace TOML restore for the per-org configuration, split
    /// out of <see cref="OrganizationConfigService"/> and sharing its cache.
    /// </summary>
    public OrganizationConfigTomlImporter NewOrganizationConfigTomlImporter(AppDbContext ctx) =>
        new(ctx, OrgContext, NewQuotaGuard(ctx), NewOrganizationConfigService(ctx),
            NullLogger<OrganizationConfigTomlImporter>.Instance);

    /// <summary>
    /// The per-org machine-translation settings (provider, trigger and the
    /// encrypted key), split out of <see cref="OrganizationConfigService"/> and
    /// sharing its cache.
    /// </summary>
    public ALDevToolbox.Services.Translation.MachineTranslationSettingsService NewMachineTranslationSettingsService(AppDbContext ctx) =>
        new(ctx, OrgContext, NewOrganizationConfigService(ctx),
            NullLogger<ALDevToolbox.Services.Translation.MachineTranslationSettingsService>.Instance,
            DataProtectionProvider);

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> whose scopes hand out a fresh
    /// <see cref="AppDbContext"/> on this fixture's database.
    /// </summary>
    /// <remarks>
    /// <see cref="OrganizationConfigService.GetOrganizationNameAsync"/> runs its
    /// cache-miss read on a context of its own rather than the caller's, because
    /// that read happens in MainLayout and would otherwise have two commands in
    /// flight on one scoped context alongside the page's own queries (#551).
    /// Tests that construct the service by hand need somewhere for that context
    /// to come from, and it has to be the same database.
    /// </remarks>
    public IServiceScopeFactory ScopeFactory => _scopeProvider.Value.GetRequiredService<IServiceScopeFactory>();

    private readonly Lazy<ServiceProvider> _scopeProvider;

    /// <summary>
    /// Per-org admin toggles + email-domain allow-list, split out of
    /// <see cref="OrganizationConfigService"/>. Shares the same per-fixture
    /// cache (via the config service it delegates invalidation to) so the
    /// isolation note above still holds.
    /// </summary>
    public OrganizationAdminService NewOrganizationAdminService(AppDbContext ctx, IHttpContextAccessor? http = null) =>
        new(ctx, OrgContext, McpAvailability,
            new ALDevToolbox.Services.Account.AuthService(ctx, NullLogger<ALDevToolbox.Services.Account.AuthService>.Instance, TimeProvider.System),
            NewOrganizationConfigService(ctx), DataProtectionProvider,
            NewToolEnablement(ctx), http ?? new FixedHttpContextAccessor(null),
            NullLogger<OrganizationAdminService>.Instance);

    /// <summary>
    /// An accessor that returns exactly the context it was given. The real
    /// <see cref="HttpContextAccessor"/> keeps its context in an AsyncLocal
    /// shared by every instance in the flow, so two of them in one test would
    /// answer with whichever was set last.
    /// </summary>
    public sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public FixedHttpContextAccessor(HttpContext? context) => HttpContext = context;
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>A request whose cookie session had a second factor at <paramref name="strongAt"/>.</summary>
    public static FixedHttpContextAccessor RequestWithStrongAuth(DateTime strongAt) =>
        new(new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(ALDevToolbox.Services.Account.StepUpAuth.StrongAuthAtClaim,
                    strongAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture))], "test")),
        });

    /// <summary>
    /// The per-organisation GitHub App connection. Shares this fixture's
    /// config service (and therefore its cache) so a Connect made here is
    /// visible to the next read, as it is in the app.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubConnectionService NewGitHubConnectionService(
        AppDbContext ctx, ALDevToolbox.Services.GitHub.GitHubAccessService access) =>
        new(ctx, OrgContext, NewOrganizationConfigService(ctx), NewSystemSettingsService(ctx), access,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubConnectionService>.Instance, TimeProvider.System);

    /// <summary>
    /// The per-user GitHub account link. Takes the API client so a test can
    /// decide what GitHub answers; <paramref name="clock"/> lets the token-expiry
    /// tests move time without waiting eight hours.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubAccessService NewGitHubAccessService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        TimeProvider? clock = null) =>
        new(ctx, client, OrgContext, NewOrganizationConfigService(ctx), DataProtectionProvider,
            clock ?? TimeProvider.System,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubAccessService>.Instance);

    /// <summary>
    /// A <see cref="ALDevToolbox.Services.GitHub.GitHubAppClient"/> whose HTTP
    /// goes to <paramref name="handler"/> instead of api.github.com, wired with
    /// the same base address and headers <c>GitHubRegistration</c> configures.
    /// A stub handler never redirects, so the no-auto-redirect rule that makes
    /// GitHub's 302 "you are not in this organisation" answer visible is the
    /// registration's business, not this fixture's.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubAppClient NewGitHubAppClient(
        AppDbContext ctx, HttpMessageHandler handler, TimeProvider? clock = null)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(ALDevToolbox.Services.GitHub.GitHubAppClient.ApiBaseUrl),
        };
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox");
        return new ALDevToolbox.Services.GitHub.GitHubAppClient(
            http, NewSystemSettingsService(ctx), _memoryCache, clock ?? TimeProvider.System,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubAppClient>.Instance);
    }

    /// <summary>
    /// A <see cref="SystemSettingsService"/> on this fixture's context and
    /// in-memory key ring. The singleton row is created on first write.
    /// </summary>
    public SystemSettingsService NewSystemSettingsService(AppDbContext ctx) =>
        new(ctx, DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);

    /// <summary>
    /// Per-fixture MCP availability state. Defaults to enabled so tests that
    /// don't care about the toggle behave as if the SiteAdmin has flipped it
    /// on. Tests that care (the org-level toggle tests) flip this directly.
    /// </summary>
    public ALDevToolbox.Services.Mcp.McpAvailabilityState McpAvailability { get; } = CreateMcpAvailability();

    private static ALDevToolbox.Services.Mcp.McpAvailabilityState CreateMcpAvailability()
    {
        var state = new ALDevToolbox.Services.Mcp.McpAvailabilityState();
        state.Set(true);
        return state;
    }

    /// <summary>
    /// Registers the storage-quota service chain on the supplied collection
    /// for bUnit component tests that exercise services depending on
    /// <see cref="StorageQuotaGuard"/> (any service that mutates tenanted
    /// state). Idempotent: adds TimeProvider, IDataProtectionProvider,
    /// IMemoryCache, SystemSettingsService, DatabaseUsageService, and
    /// StorageQuotaGuard. With no system_settings row and no per-org
    /// override the guard treats every operation as unlimited.
    /// </summary>
    public void AddStorageServices(IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(DataProtectionProvider);
        services.TryAddSingleton<IMemoryCache>(_memoryCache);
        services.TryAddSingleton<ALDevToolbox.Services.Mcp.IMcpAvailability>(McpAvailability);
        services.TryAddSingleton<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>(
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false));
        services.AddScoped<SystemSettingsService>();
        services.AddScoped<DatabaseUsageService>();
        services.AddScoped<StorageQuotaGuard>();
        // OrganizationConfigService depends on AuthService for the
        // strong-auth foot-gun guard on SetRequireStrongAuthAsync.
        services.AddScoped<ALDevToolbox.Services.Account.AuthService>();
    }

    /// <summary>
    /// The repository picker's data source: the installation's list narrowed to
    /// what one person can see. Takes the pieces so a test can decide what
    /// GitHub answers and who is asking.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubRepositoryService NewGitHubRepositoryService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access) =>
        new(client, access, NewGitHubConnectionService(ctx, access), OrgContext,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubRepositoryService>.Instance);

    /// <summary>"Add to repository": generation, the access gate, and the commit.</summary>
    public ALDevToolbox.Services.GitHub.GitHubExtensionDeliveryService NewGitHubExtensionDeliveryService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access) =>
        new(NewGenerationService(ctx), new WorkspaceConfigService(ctx), NewGitHubRepositoryService(ctx, client, access),
            access, client, OrgContext,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubExtensionDeliveryService>.Instance);

    /// <summary>"Create repository": generation, the membership gate, and the first commit.</summary>
    /// <param name="toolAvailability">
    /// The site-wide tool toggles the service asks before it registers a
    /// solution. Defaults to everything enabled; hand in a state with
    /// <c>Projects</c> switched off to exercise the site-disabled half of #772.
    /// </param>
    public ALDevToolbox.Services.GitHub.GitHubWorkspaceRepositoryService NewGitHubWorkspaceRepositoryService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access,
        ALDevToolbox.Services.Tools.IToolAvailability? toolAvailability = null,
        ILogger<ALDevToolbox.Services.GitHub.GitHubWorkspaceRepositoryService>? logger = null) =>
        new(NewGenerationService(ctx), NewGitHubRepositoryService(ctx, client, access),
            NewGitHubConnectionService(ctx, access), access, client,
            NewGitHubRepositoryStandardsService(ctx), NewProjectService(ctx),
            NewOrganizationConfigService(ctx), NewToolEnablement(ctx, toolAvailability), ctx, OrgContext,
            logger ?? NullLogger<ALDevToolbox.Services.GitHub.GitHubWorkspaceRepositoryService>.Instance);

    /// <summary>
    /// The "is this tool switched on for the acting organisation" service. There
    /// is no HttpContext in a test, so it answers from the organisation row -
    /// which is what an MCP request does too.
    /// </summary>
    public ALDevToolbox.Services.Tools.ToolEnablement NewToolEnablement(
        AppDbContext ctx, ALDevToolbox.Services.Tools.IToolAvailability? toolAvailability = null) =>
        new(toolAvailability ?? EverythingEnabled(),
            new Microsoft.AspNetCore.Http.HttpContextAccessor(), ctx, OrgContext, TimeProvider.System);

    /// <summary>A site-level toggle state with nothing switched off.</summary>
    public static ALDevToolbox.Services.Tools.ToolAvailabilityState EverythingEnabled() =>
        new(new AlwaysOnMcp());

    private sealed class AlwaysOnMcp : ALDevToolbox.Services.Mcp.IMcpAvailability
    {
        public bool IsEnabled => true;
    }
    /// <summary>
    /// GitHub Releases in both directions (#632): publishing a build's app files to a
    /// repository's Releases page, and staging a Release's files back as a build.
    /// <paramref name="publicOrigin"/> is what the release body links builds with; the
    /// default leaves it unset, which is the deployment that has not configured one.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubReleaseService NewGitHubReleaseService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access,
        string? publicOrigin = null,
        TimeProvider? clock = null) =>
        new(ctx, client, NewGitHubConnectionService(ctx, access),
            new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, OrgContext), OrgContext,
            new ALDevToolbox.Endpoints.PublicOrigin(publicOrigin),
            clock ?? TimeProvider.System,
            NewToolEnablement(ctx),
            NullLogger<ALDevToolbox.Services.GitHub.GitHubReleaseService>.Instance);
    /// A <see cref="ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService"/> on this
    /// fixture's context, with the discovery queue nobody drains here.
    /// </summary>
    public ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService NewProjectService(AppDbContext ctx)
    {
        var projectAccess = new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, OrgContext);
        var extensionDiscovery = new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService(
            ctx, OrgContext, projectAccess, new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue(),
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>.Instance);
        return new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService(
            ctx, OrgContext, projectAccess, extensionDiscovery,
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>.Instance);
    }

    /// <summary>
    /// Repository discovery (#629): the organisation-wide sweep, the panel's
    /// narrowed read, and Track / Ignore. Builds its own ProjectService chain,
    /// because tracking a repository creates a solution.
    /// </summary>
    public ALDevToolbox.Services.GitHub.RepositoryDiscoveryService NewRepositoryDiscoveryService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access,
        TimeProvider? clock = null)
    {
        var projectAccess = new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, OrgContext);
        var extensionDiscovery = new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService(
            ctx, OrgContext, projectAccess, new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue(),
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>.Instance);
        var projects = new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService(
            ctx, OrgContext, projectAccess, extensionDiscovery,
            NullLogger<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>.Instance);
        return new ALDevToolbox.Services.GitHub.RepositoryDiscoveryService(
            ctx, client, access, NewGitHubConnectionService(ctx, access), NewOrganizationConfigService(ctx),
            projects, OrgContext, clock ?? TimeProvider.System,
            NullLogger<ALDevToolbox.Services.GitHub.RepositoryDiscoveryService>.Instance);
    }

    /// <summary>
    /// Dependency drift (#630): the scan a first-party release triggers, what the
    /// Solutions panel reads, and the pull requests that bump the manifests.
    /// <paramref name="publicOrigin"/> is what the pull-request body links the
    /// release comparison with; the default leaves it unset, which is the
    /// deployment that has not configured one.
    /// </summary>
    public ALDevToolbox.Services.GitHub.DependencyDriftService NewDependencyDriftService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access,
        string? publicOrigin = null,
        TimeProvider? clock = null) =>
        new(ctx, client, access, NewGitHubConnectionService(ctx, access),
            NewGitHubRepositoryService(ctx, client, access),
            new CatalogService(ctx, NullLogger<CatalogService>.Instance, OrgContext),
            new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, OrgContext),
            new ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerLinks(),
            new ALDevToolbox.Endpoints.PublicOrigin(publicOrigin),
            OrgContext,
            clock ?? TimeProvider.System,
            NullLogger<ALDevToolbox.Services.GitHub.DependencyDriftService>.Instance);


    /// <summary>
    /// The per-organisation repository standards (#628): the files every
    /// created repository gets, and the branch ruleset applied to it.
    /// </summary>
    public ALDevToolbox.Services.GitHub.GitHubRepositoryStandardsService NewGitHubRepositoryStandardsService(
        AppDbContext ctx) =>
        new(ctx, NewOrganizationConfigService(ctx), OrgContext,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubRepositoryStandardsService>.Instance);
    /// <summary>The Translator's repository round trip: list, open, save back.</summary>
    public ALDevToolbox.Services.GitHub.GitHubTranslationService NewGitHubTranslationService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access) =>
        new(NewGitHubRepositoryService(ctx, client, access), access, client, OrgContext,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubTranslationService>.Instance);

    /// <summary>The Cookbook's recipe service, wired to this fixture's database.</summary>
    public RecipeService NewRecipeService(AppDbContext ctx) =>
        new(ctx, NullLogger<RecipeService>.Instance, OrgContext, NewQuotaGuard(ctx));

    /// <summary>"Apply a recipe to a repository": the access gate, the commit, and the attribution row.</summary>
    public ALDevToolbox.Services.GitHub.GitHubRecipeDeliveryService NewGitHubRecipeDeliveryService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access) =>
        new(NewRecipeService(ctx), NewGitHubRepositoryService(ctx, client, access), access, client, OrgContext,
            NewToolEnablement(ctx),
            NullLogger<ALDevToolbox.Services.GitHub.GitHubRecipeDeliveryService>.Instance);

    /// <summary>The workspace / extension generator, wired to this fixture's database.</summary>
    public GenerationService NewGenerationService(AppDbContext ctx)
    {
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        return new GenerationService(
            ctx,
            NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
    }

    /// <summary>
    /// Registers the GitHub services a component under test injects, with
    /// <paramref name="handler"/> standing in for api.github.com. Pass a
    /// <c>FakeGitHubApi</c> to say what GitHub answers; pass nothing and every
    /// call fails, which is the right default for a page that only has to
    /// render its "GitHub is not set up" state.
    /// </summary>
    public void AddGitHubServices(IServiceCollection services, HttpMessageHandler? handler = null)
    {
        services.AddScoped(sp => NewGitHubAppClient(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), handler ?? new UnreachableHandler()));
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubAccessService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubConnectionService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubRepositoryService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubExtensionDeliveryService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubWorkspaceRepositoryService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubRepositoryStandardsService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubTranslationService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubRecipeDeliveryService>();
        services.AddScoped<ALDevToolbox.Services.GitHub.GitHubReleaseService>();
        services.TryAddSingleton(new ALDevToolbox.Endpoints.PublicOrigin(null));
        services.AddScoped<ALDevToolbox.Services.GitHub.RepositoryDiscoveryService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerLinks>();
        services.AddScoped<ALDevToolbox.Services.GitHub.DependencyDriftService>();
        // "Create repository" asks whether this organisation uses Solutions
        // before it registers one (#772), so the toggles have to resolve too.
        AddToolServices(services);
    }

    /// <summary>
    /// The tool toggles and the service that reads them, with nothing switched
    /// off site-wide. A test that wants a tool off either switches it off on the
    /// organisation row or registers its own
    /// <see cref="ALDevToolbox.Services.Tools.IToolAvailability"/> first.
    /// </summary>
    public static void AddToolServices(IServiceCollection services)
    {
        services.TryAddSingleton<ALDevToolbox.Services.Tools.IToolAvailability>(EverythingEnabled());
        services.TryAddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor,
            Microsoft.AspNetCore.Http.HttpContextAccessor>();
        services.TryAddScoped<ALDevToolbox.Services.Tools.ToolEnablement>();
    }

    /// <summary>
    /// The translation-memory ingest (#631): the org's GitHub connection, the
    /// API client, and the memory it feeds. Reads go out on the installation
    /// token, so no user link is involved.
    /// </summary>
    public ALDevToolbox.Services.Translation.TranslationMemoryIngestService NewTranslationMemoryIngestService(
        AppDbContext ctx,
        ALDevToolbox.Services.GitHub.GitHubAppClient client,
        ALDevToolbox.Services.GitHub.GitHubAccessService access,
        TimeProvider? clock = null) =>
        new(ctx, NewGitHubConnectionService(ctx, access), client,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, OrgContext,
                NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            OrgContext, clock ?? TimeProvider.System,
            NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryIngestService>.Instance);

    /// <summary>Stands in for a GitHub that cannot be reached at all.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("api.github.com is not reachable from tests.");
    }

    /// <summary>
    /// Returns a <see cref="StorageQuotaGuard"/> wired to the per-fixture
    /// context. With no system-settings row and no per-org quota override
    /// the guard treats every operation as unlimited, so tests that don't
    /// care about quotas pass through. Tests that DO care should set the
    /// system_settings row or the organisation override before exercising
    /// the guarded path.
    /// </summary>
    /// <param name="orgContext">
    /// Overrides the fixture's own context — used by tests that need the guard to read
    /// the ambient background-worker identity instead of a request's claims.
    /// </param>
    public StorageQuotaGuard NewQuotaGuard(
        AppDbContext ctx, bool singleTenant = false, IOrganizationContext? orgContext = null)
    {
        var usage = NewDatabaseUsageService(ctx);
        return new StorageQuotaGuard(
            usage, orgContext ?? OrgContext, _memoryCache,
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(singleTenant),
            NullLogger<StorageQuotaGuard>.Instance);
    }

    /// <summary>
    /// Returns a <see cref="DatabaseUsageService"/> wired to the per-fixture
    /// context. With no system-settings row it treats storage as unlimited;
    /// tests exercising the snapshot recompute/read path use this directly.
    /// </summary>
    public DatabaseUsageService NewDatabaseUsageService(AppDbContext ctx)
    {
        var systemSettings = NewSystemSettingsService(ctx);
        return new DatabaseUsageService(
            ctx, systemSettings, OrgContext, NullLogger<DatabaseUsageService>.Instance, TimeProvider.System);
    }

    /// <summary>
    /// Returns an <see cref="AuditInterceptor"/> wired to an empty
    /// <see cref="IHttpContextAccessor"/>. Audit rows attribute changes to
    /// "unknown" unless the test installs a principal on the accessor first.
    /// </summary>
    public static AuditInterceptor NewAuditInterceptor() =>
        new(new HttpContextAccessor());

    /// <summary>
    /// In-memory <see cref="IDataProtectionProvider"/> for tests that need to
    /// encrypt / decrypt round-trip — e.g. <c>SystemSettingsService</c>'s
    /// SMTP password. Lazy-initialised once per fixture.
    /// </summary>
    public IDataProtectionProvider DataProtectionProvider => _dpProvider.Value;

    private readonly Lazy<IDataProtectionProvider> _dpProvider = new(() =>
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    });

}

/// <summary>
/// Process-wide handle to the Postgres instance backing <see cref="TestDb"/>.
/// Either wraps a runner-provided service container (CI) or a Testcontainers
/// instance started on first use (local dev). Container lifetime equals the
/// test process; Testcontainers' Resource Reaper handles the local-dev cleanup
/// when a process exits abnormally.
/// </summary>
internal sealed class PostgresHost
{
    private readonly string _adminConnectionString;
    private readonly PostgreSqlContainer? _container;

    private PostgresHost(string adminConnectionString, PostgreSqlContainer? container)
    {
        _adminConnectionString = adminConnectionString;
        _container = container;
    }

    public static PostgresHost Start()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ALDT_TEST_POSTGRES_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new PostgresHost(fromEnv, container: null);
        }

        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .Build();
        container.StartAsync().GetAwaiter().GetResult();
        return new PostgresHost(container.GetConnectionString(), container);
    }

    public string ConnectionStringFor(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = database };
        return builder.ConnectionString;
    }

    public void CreateDatabase(string name)
    {
        using var conn = new NpgsqlConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Quoting the identifier is sufficient because `name` is a fresh
        // GUID-derived string we control — never user input.
        cmd.CommandText = $"CREATE DATABASE \"{name}\"";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clones <paramref name="template"/> into a new database. Postgres refuses
    /// this while any session is connected to the template, and the suite runs
    /// fixtures in parallel, so a clone that loses that race retries rather than
    /// failing the test. The template itself is only ever written once, by
    /// <c>TestDb.BuildTemplate</c> over a non-pooled connection.
    /// </summary>
    public void CloneDatabase(string name, string template)
    {
        const int maxAttempts = 40;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var conn = new NpgsqlConnection(_adminConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Both identifiers are GUID-derived strings we control, never
                // user input, so quoting them is sufficient.
                cmd.CommandText = $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"";
                cmd.ExecuteNonQuery();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < maxAttempts)
            {
                // 55006: another fixture is mid-clone, or a connection to the
                // template has not closed yet. Back off briefly and retry.
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }

    public void DropDatabase(string name)
    {
        using var conn = new NpgsqlConnection(_adminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // WITH (FORCE) terminates any lingering sessions so DROP doesn't block
        // on the just-disposed test connections.
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}
