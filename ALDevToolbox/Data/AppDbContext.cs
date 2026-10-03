using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Data;

/// <summary>
/// The single EF Core context for the application. Holds the database sets and
/// configures the table layout described in <c>.design/domain-model.md</c>:
/// snake_case column names, JSON-text value objects, soft-delete columns and
/// the audit log.
///
/// Per-entity fluent configuration lives in <c>Data/Configurations/</c> as
/// individual <see cref="IEntityTypeConfiguration{TEntity}"/> classes; this
/// type only wires DbSets, save-time invariants, and registers the
/// configurations.
///
/// Multi-tenant scoping (Milestone 13): every editable table carries an
/// <c>organization_id</c>. The per-entity configurations install query
/// filters that narrow reads to
/// <see cref="IOrganizationContext.CurrentOrganizationId"/>; pre-login flows
/// (login, signup, bootstrap, seed) bypass with <c>IgnoreQueryFilters()</c>.
/// </summary>
public class AppDbContext : DbContext
{
    private readonly IOrganizationContext _orgContext;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        _orgContext = NullOrganizationContext.Instance;
    }

    /// <summary>
    /// The constructor DI must use. <c>IDbContextFactory&lt;AppDbContext&gt;</c>
    /// (registered in <c>DatabaseRegistration</c> for the audit reads, issue
    /// #741) builds contexts through <c>ActivatorUtilities</c>, which refuses to
    /// choose between our two public constructors on its own — and silently
    /// picking the one-argument one would hand the context the null
    /// organisation context and take the tenant query filter out of play. The
    /// attribute pins the choice; <c>AppDbContextFactoryConstructorTests</c>
    /// pins it in a test.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public AppDbContext(DbContextOptions<AppDbContext> options, IOrganizationContext orgContext) : base(options)
    {
        _orgContext = orgContext;
    }

    /// <summary>
    /// Propagates the denormalised <c>workspace_extension_id</c> /
    /// <c>module_id</c> columns down the recursive folder tree before save.
    /// EF only sets the FK column on direct navigation children — i.e. it
    /// wires <c>extension.Folders</c> and <c>folder.ParentFolder</c>, but
    /// doesn't carry the extension's id past the first hop. The migration's
    /// data-rewrite block populates the column row-by-row; application writes
    /// have to do the same. We walk the parent chain of every added folder so
    /// that nested rows land with the right FK value, matching the unique-root
    /// and unique-sibling indexes set up in the folder configurations.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PropagateExtensionFolderIds();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        PropagateExtensionFolderIds();
        return base.SaveChanges();
    }

    private void PropagateExtensionFolderIds()
    {
        foreach (var entry in ChangeTracker.Entries<WorkspaceExtensionFolder>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var folder = entry.Entity;
            if (folder.ParentFolder is null) continue;

            // Walk up to the root and copy its extension reference. The root's
            // extension nav is set by the EF parent relationship at save time,
            // so by the time SaveChanges runs the chain is well-formed.
            var root = folder.ParentFolder;
            while (root.ParentFolder is not null) root = root.ParentFolder;
            if (root.Extension is not null)
            {
                folder.Extension = root.Extension;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ModuleExtensionFolder>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var folder = entry.Entity;
            if (folder.ParentFolder is null) continue;

            var root = folder.ParentFolder;
            while (root.ParentFolder is not null) root = root.ParentFolder;
            if (root.Module is not null)
            {
                folder.Module = root.Module;
            }
        }
    }

    /// <summary>
    /// Sentinel <see cref="IOrganizationContext"/> used when the context is
    /// constructed without one (design-time tooling). Filters never match.
    /// </summary>
    private sealed class NullOrganizationContext : IOrganizationContext
    {
        public static readonly NullOrganizationContext Instance = new();
        public int? CurrentOrganizationId => null;
        public int? CurrentUserId => null;
        public bool IsSiteAdmin => false;
        public bool IsSystemOrganization => false;
        public int OrganizationIdForFilter => 0;
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationEmailDomain> OrganizationEmailDomains => Set<OrganizationEmailDomain>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SignupRequest> SignupRequests => Set<SignupRequest>();
    // Pre-account email verification (email-first signup). Org-less and
    // user-less, so deliberately NOT scoped by the tenant query filter — and
    // with no filter on the table there is nothing for its reads to escape,
    // so no IgnoreQueryFilters() belongs on its read path.
    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    // Transactional email waiting to be sent (issue #790). Written by pre-auth
    // flows that have no organisation in scope and read by a cross-org SiteAdmin
    // console, so deliberately NOT scoped by the tenant query filter - and with
    // no filter on the table there is nothing for its reads to escape, so no
    // IgnoreQueryFilters() belongs on its read path. The organization_id it does
    // carry is a label for that console, never a fence.
    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<UserTotpSecret> UserTotpSecrets => Set<UserTotpSecret>();
    public DbSet<UserRecoveryCode> UserRecoveryCodes => Set<UserRecoveryCode>();
    public DbSet<UserPasskey> UserPasskeys => Set<UserPasskey>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<UserRepositoryToken> UserRepositoryTokens => Set<UserRepositoryToken>();
    public DbSet<UserNotificationSetting> UserNotificationSettings => Set<UserNotificationSetting>();
    public DbSet<NotificationDigestItem> NotificationDigestItems => Set<NotificationDigestItem>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<UserExternalLogin> UserExternalLogins => Set<UserExternalLogin>();

    // Teams and their membership — see .design/teams-and-visibility.md.
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<RuntimeTemplate> RuntimeTemplates => Set<RuntimeTemplate>();
    public DbSet<WorkspaceExtension> WorkspaceExtensions => Set<WorkspaceExtension>();
    public DbSet<WorkspaceExtensionFolder> WorkspaceExtensionFolders => Set<WorkspaceExtensionFolder>();
    public DbSet<WorkspaceExtensionFile> WorkspaceExtensionFiles => Set<WorkspaceExtensionFile>();
    public DbSet<WorkspaceExtensionDependency> WorkspaceExtensionDependencies => Set<WorkspaceExtensionDependency>();
    public DbSet<ModuleExtensionFolder> ModuleExtensionFolders => Set<ModuleExtensionFolder>();
    public DbSet<ModuleExtensionFile> ModuleExtensionFiles => Set<ModuleExtensionFile>();
    public DbSet<RuntimeTemplateDefaultModule> RuntimeTemplateDefaultModules => Set<RuntimeTemplateDefaultModule>();
    public DbSet<RuntimeTemplateIncludedFile> RuntimeTemplateIncludedFiles => Set<RuntimeTemplateIncludedFile>();
    public DbSet<RuntimeTemplateRootFolder> RuntimeTemplateRootFolders => Set<RuntimeTemplateRootFolder>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleDependency> ModuleDependencies => Set<ModuleDependency>();
    public DbSet<WellKnownDependency> WellKnownDependencies => Set<WellKnownDependency>();
    public DbSet<ApplicationVersion> ApplicationVersions => Set<ApplicationVersion>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<OrganizationAsset> OrganizationAssets => Set<OrganizationAsset>();
    public DbSet<OrganizationFile> OrganizationFiles => Set<OrganizationFile>();
    public DbSet<GitHubRepositoryStandardFile> GitHubRepositoryStandardFiles => Set<GitHubRepositoryStandardFile>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<PerTenantBackup> PerTenantBackups => Set<PerTenantBackup>();
    public DbSet<OrganizationUsageSnapshot> OrganizationUsageSnapshots => Set<OrganizationUsageSnapshot>();
    // AL repositories the discovery sweep found in the connected GitHub
    // organisation that no solution tracks yet — see .design/github-integration-phase2.md.
    public DbSet<GitHubRepositoryCandidate> GitHubRepositoryCandidates => Set<GitHubRepositoryCandidate>();
    // What a tracked repository's app.json is behind on, as the last drift scan
    // found it — see .design/github-integration-phase2.md.
    public DbSet<GitHubRepositoryDrift> GitHubRepositoryDrift => Set<GitHubRepositoryDrift>();
    // Object Explorer (.app ingest) — see .design/object-explorer.md.
    public DbSet<OeRelease> OeReleases => Set<OeRelease>();
    // Vendor Releases a pipeline build resolved symbols from (#901).
    public DbSet<OeReleaseDependency> OeReleaseDependencies => Set<OeReleaseDependency>();
    public DbSet<OeModule> OeModules => Set<OeModule>();
    public DbSet<OeModuleFile> OeModuleFiles => Set<OeModuleFile>();
    public DbSet<OeFileContent> OeFileContents => Set<OeFileContent>();
    public DbSet<OeModuleObject> OeModuleObjects => Set<OeModuleObject>();
    public DbSet<OeModuleSymbol> OeModuleSymbols => Set<OeModuleSymbol>();
    public DbSet<OeModuleVariable> OeModuleVariables => Set<OeModuleVariable>();
    public DbSet<OeModuleReference> OeModuleReferences => Set<OeModuleReference>();
    public DbSet<OeModuleSystemReference> OeModuleSystemReferences => Set<OeModuleSystemReference>();
    public DbSet<OeModuleTranslation> OeModuleTranslations => Set<OeModuleTranslation>();
    public DbSet<OeImportJob> OeImportJobs => Set<OeImportJob>();
    // Cached Microsoft artifact index (available OnPrem builds per country).
    public DbSet<OeArtifactVersion> OeArtifactVersions => Set<OeArtifactVersion>();
    public DbSet<OeProject> OeProjects => Set<OeProject>();
    public DbSet<OeProjectEnvironment> OeProjectEnvironments => Set<OeProjectEnvironment>();
    public DbSet<OePipeline> OePipelines => Set<OePipeline>();
    public DbSet<OeReleasePipeline> OeReleasePipelines => Set<OeReleasePipeline>();
    public DbSet<OeProjectDelivery> OeProjectDeliveries => Set<OeProjectDelivery>();
    public DbSet<OeProjectDeliveryResult> OeProjectDeliveryResults => Set<OeProjectDeliveryResult>();
    public DbSet<OeProjectRepository> OeProjectRepositories => Set<OeProjectRepository>();
    public DbSet<OeProjectBuildResult> OeProjectBuildResults => Set<OeProjectBuildResult>();
    public DbSet<OeProjectSymbol> OeProjectSymbols => Set<OeProjectSymbol>();
    // Teams assigned to a project — the visibility grant. See .design/teams-and-visibility.md.
    public DbSet<OeProjectTeam> OeProjectTeams => Set<OeProjectTeam>();
    // The Customer tab's hand-kept lists. See .design/solution-customer-info.md.
    public DbSet<OeProjectContact> OeProjectContacts => Set<OeProjectContact>();
    public DbSet<OeProjectPerson> OeProjectPeople => Set<OeProjectPerson>();
    public DbSet<OeProjectIntegration> OeProjectIntegrations => Set<OeProjectIntegration>();
    // Customer modules: the catalogue, what was typed in, and what environments report.
    public DbSet<CustomerModule> CustomerModules => Set<CustomerModule>();
    public DbSet<OeProjectModule> OeProjectModules => Set<OeProjectModule>();
    public DbSet<OeEnvironmentApp> OeEnvironmentApps => Set<OeEnvironmentApp>();
    // Artifacts tool — first-class builds split off Release (see .design/artifacts.md).
    public DbSet<OeProjectBuild> OeProjectBuilds => Set<OeProjectBuild>();
    public DbSet<OeProjectBuildRepoCommit> OeProjectBuildRepoCommits => Set<OeProjectBuildRepoCommit>();
    public DbSet<OeProjectBuildCommit> OeProjectBuildCommits => Set<OeProjectBuildCommit>();
    public DbSet<OeProjectBuildArtifact> OeProjectBuildArtifacts => Set<OeProjectBuildArtifact>();
    public DbSet<OeProjectBuildLog> OeProjectBuildLogs => Set<OeProjectBuildLog>();
    public DbSet<OeProjectBuildDiagnostic> OeProjectBuildDiagnostics => Set<OeProjectBuildDiagnostic>();
    // Branch watching (#963): where each solution repository's branches point, and
    // what merged into them, as GitHub's push and pull_request webhooks said.
    public DbSet<OeRepositoryBranchHead> OeRepositoryBranchHeads => Set<OeRepositoryBranchHead>();
    public DbSet<OeRepositoryMergedPullRequest> OeRepositoryMergedPullRequests => Set<OeRepositoryMergedPullRequest>();
    // What the upgrade team did (or scheduled) to a customer's environment — the rows
    // behind the per-environment activity feed. See .design/saas-delivery.md.
    public DbSet<OeEnvironmentUpgradeAction> OeEnvironmentUpgradeActions => Set<OeEnvironmentUpgradeAction>();
    // Planned upgrades (#984): a named wave of environments moved, started and checked
    // together. See .design/environment-updates.md, "Planned upgrades".
    public DbSet<OeEnvironmentUpgrade> OeEnvironmentUpgrades => Set<OeEnvironmentUpgrade>();
    public DbSet<OeEnvironmentUpgradeLine> OeEnvironmentUpgradeLines => Set<OeEnvironmentUpgradeLine>();
    // Translator tool — cross-source translation memory (see .design/translator/).
    public DbSet<TranslationMemoryEntry> TranslationMemory => Set<TranslationMemoryEntry>();
    public DbSet<TranslationMemoryVote> TranslationMemoryVotes => Set<TranslationMemoryVote>();
    // Which file in which repository each learned pair last came from, so the
    // nightly ingest reads only what has changed (#631).
    public DbSet<TranslationMemorySource> TranslationMemorySources => Set<TranslationMemorySource>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeFile> RecipeFiles => Set<RecipeFile>();
    public DbSet<RecipeDownload> RecipeDownloads => Set<RecipeDownload>();
    public DbSet<RecipeSuggestion> RecipeSuggestions => Set<RecipeSuggestion>();
    public DbSet<RecipeSuggestionFile> RecipeSuggestionFiles => Set<RecipeSuggestionFile>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    /// <summary>
    /// The mirrored BCQuality knowledge base (see <c>.design/bcquality.md</c>).
    /// System-level content with no <c>organization_id</c> and, deliberately,
    /// no query filter — it is public Microsoft guidance, byte-identical for
    /// every tenant. Because there is no filter there is nothing to escape:
    /// the read path must never reach for <c>IgnoreQueryFilters()</c>.
    /// </summary>
    public DbSet<BcQualityArticle> BcQualityArticles => Set<BcQualityArticle>();
    public DbSet<BcQualityArticleSample> BcQualityArticleSamples => Set<BcQualityArticleSample>();
    public DbSet<BcQualityIngestState> BcQualityIngestState => Set<BcQualityIngestState>();

    /// <summary>
    /// Per-user, per-organisation "trust this OAuth client" record. The
    /// OpenIddict-managed token tables (oauth_applications, _authorizations,
    /// _scopes, _tokens) are registered via <c>modelBuilder.UseOpenIddict()</c>
    /// in <see cref="OnModelCreating"/>; this is the ALDevToolbox-specific
    /// table that drives the consent screen's "already approved" auto-submit.
    /// </summary>
    public DbSet<OAuthConsent> OAuthConsents => Set<OAuthConsent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Per-entity fluent config lives in Data/Configurations/. The
        // configurations themselves are stateless — they don't install the
        // multi-tenant query filter. EF Core only re-parameterises a query
        // filter when its expression references a field/property of the
        // DbContext class itself; capturing _orgContext from a configuration
        // class would freeze the value at model-build time and leak data
        // across orgs. So filters live here, where _orgContext is "this._orgContext".
        // OpenIddict's EF Core entities. Must run BEFORE
        // ApplyConfigurationsFromAssembly so the snake_case ToTable() / column
        // overrides in Data/Configurations/OAuth/* win — UseOpenIddict() is a
        // first-write of the model, our configurations are the overrides.
        //
        // These tables are intentionally outside the multi-tenant query
        // filter: pre-auth flows (/oauth/token, /oauth/register) must read
        // them before any IOrganizationContext exists. Org attribution lives
        // in OpenIddict's free-form Properties JSON column on each row.
        modelBuilder.UseOpenIddict();

        // pg_trgm powers the Translator's fuzzy translation-memory suggestions
        // (GIN trigram index on translation_memory.source_text). Declaring the
        // extension here makes the migration emit `CREATE EXTENSION IF NOT
        // EXISTS pg_trgm`. Stays inside the "Postgres is the only persistence
        // layer" fence — it's a contrib module shipped with the standard image.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Standard tenant filter: scope every entity with an OrganizationId
        // column to the current organisation. Pre-login flows must call
        // IgnoreQueryFilters() explicitly.
        ScopeToOrganization<User>(modelBuilder);
        ScopeToOrganization<SignupRequest>(modelBuilder);
        ScopeToOrganization<Team>(modelBuilder);
        ScopeToOrganization<TeamMember>(modelBuilder);
        ScopeToOrganization<Invite>(modelBuilder, i => i.OrganizationId == _orgContext.OrganizationIdForFilter);
        ScopeToOrganization<RuntimeTemplate>(modelBuilder);
        ScopeToOrganization<ApplicationVersion>(modelBuilder);
        ScopeToOrganization<RuntimeTemplateDefaultModule>(modelBuilder);
        ScopeToOrganization<RuntimeTemplateIncludedFile>(modelBuilder);
        ScopeToOrganization<RuntimeTemplateRootFolder>(modelBuilder);
        ScopeToOrganization<WorkspaceExtension>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionFolder>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionFile>(modelBuilder);
        ScopeToOrganization<WorkspaceExtensionDependency>(modelBuilder);
        ScopeToOrganization<ModuleExtensionFolder>(modelBuilder);
        ScopeToOrganization<ModuleExtensionFile>(modelBuilder);
        ScopeToOrganization<Module>(modelBuilder);
        ScopeToOrganization<ModuleDependency>(modelBuilder);
        ScopeToOrganization<WellKnownDependency>(modelBuilder);
        ScopeToOrganization<OrganizationSettings>(modelBuilder);
        ScopeToOrganization<OrganizationAsset>(modelBuilder);
        ScopeToOrganization<OrganizationFile>(modelBuilder);
        ScopeToOrganization<GitHubRepositoryStandardFile>(modelBuilder);
        ScopeToOrganization<OrganizationEmailDomain>(modelBuilder);
        ScopeToOrganization<GitHubRepositoryCandidate>(modelBuilder);
        ScopeToOrganization<GitHubRepositoryDrift>(modelBuilder);
        ScopeToOrganization<OeRelease>(modelBuilder);
        ScopeToOrganization<OeReleaseDependency>(modelBuilder);
        ScopeToOrganization<OeModule>(modelBuilder);
        ScopeToOrganization<OeModuleFile>(modelBuilder);
        ScopeToOrganization<OeModuleObject>(modelBuilder);
        ScopeToOrganization<OeModuleSymbol>(modelBuilder);
        ScopeToOrganization<OeModuleVariable>(modelBuilder);
        ScopeToOrganization<OeModuleReference>(modelBuilder);
        ScopeToOrganization<OeModuleSystemReference>(modelBuilder);
        ScopeToOrganization<OeModuleTranslation>(modelBuilder);
        ScopeToOrganization<OeImportJob>(modelBuilder);
        ScopeToOrganization<OeArtifactVersion>(modelBuilder);
        ScopeToOrganization<OeProject>(modelBuilder);
        ScopeToOrganization<OeProjectEnvironment>(modelBuilder);
        ScopeToOrganization<OePipeline>(modelBuilder);
        ScopeToOrganization<OeReleasePipeline>(modelBuilder);
        ScopeToOrganization<OeProjectDelivery>(modelBuilder);
        ScopeToOrganization<OeProjectDeliveryResult>(modelBuilder);
        ScopeToOrganization<OeProjectRepository>(modelBuilder);
        ScopeToOrganization<OeProjectBuildResult>(modelBuilder);
        ScopeToOrganization<OeProjectSymbol>(modelBuilder);
        ScopeToOrganization<OeProjectTeam>(modelBuilder);
        ScopeToOrganization<OeProjectContact>(modelBuilder);
        ScopeToOrganization<OeProjectPerson>(modelBuilder);
        ScopeToOrganization<OeProjectIntegration>(modelBuilder);
        ScopeToOrganization<CustomerModule>(modelBuilder);
        ScopeToOrganization<OeProjectModule>(modelBuilder);
        ScopeToOrganization<OeEnvironmentApp>(modelBuilder);
        ScopeToOrganization<OeProjectBuild>(modelBuilder);
        ScopeToOrganization<OeProjectBuildRepoCommit>(modelBuilder);
        ScopeToOrganization<OeProjectBuildCommit>(modelBuilder);
        ScopeToOrganization<OeProjectBuildArtifact>(modelBuilder);
        ScopeToOrganization<OeProjectBuildLog>(modelBuilder);
        ScopeToOrganization<OeProjectBuildDiagnostic>(modelBuilder);
        ScopeToOrganization<OeRepositoryBranchHead>(modelBuilder);
        ScopeToOrganization<OeRepositoryMergedPullRequest>(modelBuilder);
        ScopeToOrganization<OeEnvironmentUpgradeAction>(modelBuilder);
        ScopeToOrganization<OeEnvironmentUpgrade>(modelBuilder);
        ScopeToOrganization<OeEnvironmentUpgradeLine>(modelBuilder);
        // NOTE: OeFileContent (oe_file_contents) is deliberately NOT scoped.
        // It is the content-addressable, cross-tenant-shared source-blob store;
        // it has no organization_id. Isolation holds because it is only ever
        // reached via the OeModuleFile.FileContent nav from an org-scoped file
        // row — never queried as a root. Do not add a filter here.
        // NOTE: the bcquality_* tables are deliberately NOT scoped. They mirror
        // a public Microsoft repository, carry no organization_id, and are the
        // same rows for every tenant. There is no filter here to escape, so no
        // IgnoreQueryFilters() call belongs anywhere on their read path.
        // See .design/bcquality.md.
        // NOTE (#701): the same "nothing to escape" rule covers every other
        // entity this method never scopes — Organization, PendingSignup,
        // LoginAttempt, SystemSettings, Backup, PerTenantBackup,
        // OrganizationUsageSnapshot, EmailOutboxMessage and OeFileContent. An IgnoreQueryFilters()
        // on a query *rooted* at one of those is a no-op that still reads to a
        // reviewer as a deliberate tenant-fence crossing, so it does not belong
        // there. The exception is a query that reaches a filtered entity from
        // such a root (an Include of a User, say) — that bypass is real.
        // IgnoreQueryFiltersUnfilteredRootTests enforces the rule.
        ScopeToOrganization<TranslationMemoryEntry>(modelBuilder);
        ScopeToOrganization<TranslationMemoryVote>(modelBuilder);
        ScopeToOrganization<TranslationMemorySource>(modelBuilder);
        ScopeToOrganization<Recipe>(modelBuilder);
        ScopeToOrganization<RecipeFile>(modelBuilder);
        ScopeToOrganization<RecipeDownload>(modelBuilder);
        ScopeToOrganization<RecipeSuggestion>(modelBuilder);
        ScopeToOrganization<RecipeSuggestionFile>(modelBuilder);
        ScopeToOrganization<PersonalAccessToken>(modelBuilder);
        ScopeToOrganization<UserRepositoryToken>(modelBuilder);
        ScopeToOrganization<UserNotificationSetting>(modelBuilder);
        ScopeToOrganization<NotificationDigestItem>(modelBuilder);
        ScopeToOrganization<UserNotification>(modelBuilder);
        ScopeToOrganization<OAuthConsent>(modelBuilder);
        // AuditLogEntry carries a *nullable* organization_id: startup seed and
        // bootstrap-admin inserts happen before any org context exists. The
        // filter therefore admits null-org rows alongside the current org's,
        // so those system rows stay visible while a tenant still cannot read
        // another tenant's audit history (#678). AuditService keeps its
        // explicit predicates as belt and braces; the SiteAdmin console reads
        // cross-org and calls IgnoreQueryFilters() explicitly.
        ScopeToOrganization<AuditLogEntry>(
            modelBuilder,
            e => e.OrganizationId == _orgContext.OrganizationIdForFilter || e.OrganizationId == null);

        // PasswordResetToken scopes via its required User principal: tokens
        // don't carry organization_id themselves, so the filter walks the nav.
        modelBuilder.Entity<PasswordResetToken>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);

        // MFA / passkey tables follow the PasswordResetToken pattern: scope via
        // the User principal. Login flows that run before the auth cookie is
        // set (TOTP / email-MFA / passkey verification) call
        // <c>IgnoreQueryFilters()</c> explicitly.
        modelBuilder.Entity<UserTotpSecret>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        modelBuilder.Entity<UserRecoveryCode>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        modelBuilder.Entity<UserPasskey>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
        // External-identity links (Entra) scope the same way. The sign-in
        // callback runs pre-auth and calls IgnoreQueryFilters() explicitly.
        modelBuilder.Entity<UserExternalLogin>()
            .HasQueryFilter(t => t.User!.OrganizationId == _orgContext.OrganizationIdForFilter);
    }

    /// <summary>
    /// Installs the standard organization-scoped query filter on
    /// <typeparamref name="T"/>. Lives on the DbContext (not on a
    /// configuration class) so the captured <c>_orgContext</c> resolves at
    /// query time via the DbContext instance — see comment in
    /// <see cref="OnModelCreating"/>.
    /// </summary>
    private void ScopeToOrganization<T>(ModelBuilder modelBuilder) where T : class
        => modelBuilder.Entity<T>().HasQueryFilter(e =>
            EF.Property<int>(e, "OrganizationId") == _orgContext.OrganizationIdForFilter);

    private void ScopeToOrganization<T>(ModelBuilder modelBuilder, System.Linq.Expressions.Expression<Func<T, bool>> filter)
        where T : class
        => modelBuilder.Entity<T>().HasQueryFilter(filter);

    /// <summary>
    /// M16: pin every <see cref="DateTime"/> column to
    /// <c>timestamp with time zone</c>. Npgsql requires <c>DateTimeKind.Utc</c>
    /// when targeting timestamptz, and the codebase already routes every
    /// write through <c>DateTime.UtcNow</c> or a UTC literal. Doing this via
    /// <see cref="ConfigureConventions"/> rather than iterating every
    /// property on every entity type keeps the model creator small (#81).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp with time zone");
        configurationBuilder.Properties<DateTime?>().HaveColumnType("timestamp with time zone");
        // #691: no bare organization_id index on the five OE fact tables. See
        // OeFactTableForeignKeyIndexConvention for why a Replace is the only
        // way — the stock convention re-creates a removed FK index.
        configurationBuilder.Conventions.Replace<ForeignKeyIndexConvention>(
            sp => new OeFactTableForeignKeyIndexConvention(
                sp.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
    }
}
