using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A customer/project entity the Artifacts tool builds. Groups one or more
/// <see cref="OeProjectRepository"/> rows (Azure DevOps or GitHub) that the
/// project-build pipeline clones, compiles, and ingests; each build is a
/// first-class <see cref="OeProjectBuild"/> that produces a <c>project</c>-kind
/// <see cref="OeRelease"/> for object navigation. Any signed-in user may create a
/// project; who may read and change an existing one depends on its
/// <see cref="Visibility"/> and the <see cref="Teams">teams</see> assigned to it —
/// the owner, org Admins, and SiteAdmins always can, and deleting stays with that
/// set alone. Org-scoped and soft-deletable. See <c>.design/artifacts.md</c> and
/// <c>.design/teams-and-visibility.md</c>.
/// </summary>
public class OeProject
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>
    /// The user who created the project — its <em>owner</em>. The owner or an org
    /// Admin may add/remove repos, edit settings, trigger builds, and delete;
    /// assigned-team members get all of that except deleting, and everyone else
    /// gets read + download (or nothing at all, when the project is
    /// <see cref="ProjectVisibility.Private"/>). Nullable (<c>ON DELETE SET NULL</c>)
    /// so a project outlives the account that created it and so legacy rows
    /// migrated from the Object-Explorer era (which had no owner) are
    /// representable — those are admin-managed until reassigned. See
    /// <c>.design/artifacts.md</c>.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>
    /// Project-facing label used to build the Release label
    /// (<c>"{Name} on BC {Major}.{Minor}"</c>). Unique per org among active rows.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional abbreviation of <see cref="Name"/>, used where the full customer
    /// name makes an unwieldy extension name ("JM Core" rather than "Jørgensen
    /// Møbler Core"). Blank means the full name is used unchanged. The generator
    /// reads it when a workspace is started from this solution; nothing derived
    /// from the name (folder, repository) ever uses it. See
    /// <c>.design/customer-naming.md</c>.
    /// </summary>
    public string? ShortName { get; set; }

    /// <summary>
    /// The solution's key in its web address - <c>/solutions/{Slug}</c> and
    /// <c>/environments/{Slug}/{environment}</c>. Lowercase ASCII words joined by
    /// dashes, unique per org among active rows, never all digits (that shape is an
    /// id). Derived from <see cref="ShortName"/>, or <see cref="Name"/> when there is none, on create and editable after; a rename
    /// leaves it alone so links keep working. See <see cref="SolutionSlug"/>.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// BC localisation/country the project's builds compile against (e.g.
    /// <c>dk</c>, or <c>w1</c> for the worldwide base). Required on create/edit
    /// since the multi-country auto-import change removed the org-wide fallback —
    /// the base app varies by localisation (regulatory features), so this is a
    /// per-project decision. Nullable only for legacy rows that predate the rule;
    /// building one fails with a friendly "set the project's country" message.
    /// </summary>
    public string? DefaultArtifactCountry { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from the admin list unless restored.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// How visible this project is outside the teams assigned to it.
    /// <see cref="ProjectVisibility.Public"/> by default — the state every project
    /// starts in and the only state valid with no <see cref="Teams">team</see>
    /// assigned. Change it through <c>ProjectService.SetAccessAsync</c>, which
    /// writes this and the team set together so the invariant can't be broken
    /// halfway. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    public ProjectVisibility Visibility { get; set; } = ProjectVisibility.Public;

    /// <summary>
    /// The teams granted access to this project. Empty exactly when
    /// <see cref="Visibility"/> is <see cref="ProjectVisibility.Public"/>.
    /// </summary>
    public ICollection<OeProjectTeam> Teams { get; set; } = new List<OeProjectTeam>();

    // ── Business Central SaaS connection (delivery) ───────────────────────
    // One Entra tenant + one set of S2S (client-credentials) credentials per
    // customer, shared across all their environments, so the connection lives on
    // the project. Used by the delivery layer to publish a build's .app files into
    // a chosen environment via BC's automation API. One Entra app per customer
    // (cross-tenant app registrations are being deprecated), so the secret is
    // first-class and short-lived. See .design/saas-delivery.md.

    // ── Customer information. See .design/solution-customer-info.md. All optional. ──

    /// <summary>
    /// Where the customer's Business Central runs. Null means nobody has said, and
    /// reads as online - see <see cref="IsOnPremises"/>.
    /// </summary>
    public ProjectHostingType? HostingType { get; set; }

    /// <summary>
    /// True when there is no Business Central admin API to call for this customer, which
    /// turns off the connection, environments, upgrades and release pipelines. Derived
    /// from <see cref="HostingType"/> so the two can never disagree.
    /// </summary>
    public bool IsOnPremises => IsOnPremisesHosting(HostingType);

    /// <summary>
    /// <see cref="IsOnPremises"/> for a hosting value read off a projection, where there
    /// is no entity to ask. The one definition, so a projected read cannot disagree with
    /// the page that reads the entity.
    /// </summary>
    public static bool IsOnPremisesHosting(ProjectHostingType? hosting) =>
        hosting is not (null or ProjectHostingType.MicrosoftCloud);

    /// <summary>What they run, as people say it ("BC 25.3", "NAV 2018 CU12"). For reading, never compared.</summary>
    public string? BcVersion { get; set; }

    public ProjectLicenseType? LicenseType { get; set; }

    public ProjectUserExperience? UserExperience { get; set; }

    /// <summary>Where a person opens the client.</summary>
    public string? ClientUrl { get; set; }

    /// <summary>
    /// Microsoft's Voice account number ("Voice ID" in a licence file): the customer's
    /// account for on-premises licence registration. Not the partner's MPN id.
    /// </summary>
    public string? VoiceAccountNumber { get; set; }

    /// <summary>
    /// The storage the customer's tenant is allowed, across all its environments, in
    /// kilobytes, as Business Central last reported it. Null until read.
    /// </summary>
    public long? BcStorageQuotaKb { get; set; }

    /// <summary>When the storage figures were last read - this and each environment's size.</summary>
    public DateTime? BcStorageFetchedAt { get; set; }

    /// <summary>How to get in: VPN, jump host, who to ask. Prose, never credentials.</summary>
    public string? AccessDescription { get; set; }

    /// <summary>Where and how it is hosted, beyond the hosting type.</summary>
    public string? HostingNotes { get; set; }

    /// <summary>Whatever support should know that fits nowhere else.</summary>
    public string? KnowledgeNotes { get; set; }

    /// <summary>The customer's Entra (AAD) tenant GUID — used for the OAuth token endpoint and to scope the admin API. Null until the connection is configured.</summary>
    public Guid? BcTenantId { get; set; }

    /// <summary>The S2S app registration's client id (one app per customer). Null until configured.</summary>
    public string? BcClientId { get; set; }

    /// <summary>
    /// The S2S client secret, encrypted with the Data Protection key ring (purpose
    /// <see cref="Services.ObjectExplorer.Bc.ProjectConnectionService.SecretProtectionPurpose"/>),
    /// mirroring the SMTP-password and repository-token precedent. Write-only in the
    /// UI ("secret is set"); never read back. Losing <c>app-keys</c> requires
    /// re-entering it. The audit interceptor redacts this column.
    /// </summary>
    public string? BcClientSecretEncrypted { get; set; }

    /// <summary>When the client secret expires (Entra secrets last at most 2 years). Surfaced as a warning before it lapses so a delivery doesn't fail on an expired secret.</summary>
    public DateTime? BcClientSecretExpiresAt { get; set; }

    /// <summary>When the credentials were last written — drives the "last updated" caption and key-ring-loss diagnostics.</summary>
    public DateTime? BcCredentialsUpdatedAt { get; set; }

    /// <summary>The customer's local IANA time zone (e.g. <c>Europe/Copenhagen</c>) so delivery scheduling defaults and "working hours" mean the customer's hours. Falls back to the org default when unset.</summary>
    public string? BcTimeZone { get; set; }

    /// <summary>Set by the "Test connection" action (an OAuth token + list-environments round-trip succeeded). Null until first verified.</summary>
    public DateTime? BcConnectionVerifiedAt { get; set; }

    /// <summary>
    /// When this project's environment list was last read from Business Central
    /// successfully, by a Test connection, a Refresh or the nightly sweep. Null until the
    /// first read. It is the freshness gate on a refresh request: a read younger than a
    /// few minutes is not asked for again unless somebody pressed Refresh themselves, so
    /// several people with the Environments list open cannot multiply the calls. See
    /// <c>.design/environment-updates.md</c>, "Freshness".
    /// </summary>
    public DateTime? BcEnvironmentsFetchedAt { get; set; }

    /// <summary>This project's fetched BC environments (the delivery targets). Populated by Test connection / Refresh.</summary>
    public ICollection<OeProjectEnvironment> Environments { get; set; } = new List<OeProjectEnvironment>();

    // ── Discovered-extensions cache (the "New/Edit pipeline" picker) ──────
    // A denormalised cache of the extensions found by a shallow clone of the
    // project's repos, so the pipeline editor's checklist appears instantly
    // instead of cloning on every open. Filled in the background when repos
    // change and on demand via Refresh. Purely a picker convenience — the build
    // re-clones and filters by the pipeline's app-ids regardless. See
    // .design/artifacts.md.

    /// <summary>Last good discovery result — a JSON array of the discovered extensions (app-id, name, publisher, version, repo). Null until first discovered.</summary>
    public string? DiscoveredExtensionsJson { get; set; }

    /// <summary>When discovery last succeeded (drives "Last discovered …"). Null until first success.</summary>
    public DateTime? DiscoveredAt { get; set; }

    /// <summary>The last discovery failure reason (no token / clone failed / no app.json), shown when there's no usable cache. Cleared on success.</summary>
    public string? DiscoveryError { get; set; }

    public ICollection<OeProjectRepository> Repositories { get; set; } = new List<OeProjectRepository>();

    /// <summary>
    /// Operator-supplied third-party symbols (<see cref="OeProjectSymbol"/>) the build
    /// merges into the symbol cache — the manual-symbols recovery path for a
    /// dependency absent from the repos' <c>.alpackages/</c>, the Microsoft artifact
    /// and the public symbol feeds. See <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    public ICollection<OeProjectSymbol> Symbols { get; set; } = new List<OeProjectSymbol>();

    /// <summary>This project's builds (newest interesting first when ordered by the service). Reaped with the project.</summary>
    public ICollection<OeProjectBuild> Builds { get; set; } = new List<OeProjectBuild>();
}
