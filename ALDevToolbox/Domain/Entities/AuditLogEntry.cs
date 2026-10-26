namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Logical type of entity that an audit row refers to. Stored as text in the
/// database via the EF Core string conversion configured on the context.
/// </summary>
public enum AuditEntityType
{
    RuntimeTemplate,
    /// <summary>
    /// Pre-unified Core folder rows. Retained for historical audit-log reads
    /// from databases that pre-date <c>UnifyExtensions</c>; new writes go to
    /// <see cref="WorkspaceExtensionFolder"/>.
    /// </summary>
    TemplateFolder,
    /// <summary>Pre-unified Core file rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateFile,
    /// <summary>Pre-unified module folder rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateModuleFolder,
    /// <summary>Pre-unified module file rows; see <see cref="TemplateFolder"/>.</summary>
    TemplateModuleFile,
    WorkspaceExtension,
    WorkspaceExtensionFolder,
    WorkspaceExtensionFile,
    WorkspaceExtensionDependency,
    ModuleExtensionFolder,
    ModuleExtensionFile,
    RuntimeTemplateDefaultModule,
    Module,
    ModuleDependency,
    WellKnownDependency,
    ApplicationVersion,
    User,
    SignupRequest,
    OrganizationSettings,
    OrganizationAsset,
    OrganizationFile,
    SystemSettings,
    Backup,
    Invite,
    Recipe,
    RecipeFile,
    RecipeSuggestion,
    RecipeSuggestionFile,
    PersonalAccessToken,
    /// <summary>A release pipeline (delivery target) — create/edit/delete of the where+how of a deploy.</summary>
    ReleasePipeline,
    /// <summary>
    /// A customer project — audited only for its Business Central connection/secret
    /// changes (the interceptor filters out discovery-cache churn and name edits).
    /// </summary>
    Project,
    /// <summary>A team — create, rename, delete. See <c>.design/teams-and-visibility.md</c>.</summary>
    Team,
    /// <summary>
    /// One person's membership of a team — added, removed, or promoted to / demoted
    /// from manager. A join row with no name of its own, so these rows stay unnamed
    /// in the log (the snapshot carries the team and user ids).
    /// </summary>
    TeamMember,
    /// <summary>
    /// One team assigned to one project — the grant that lets that team see and
    /// change it. A join row with no name of its own, so these rows stay unnamed in
    /// the log (the snapshot carries the project and team ids).
    /// </summary>
    ProjectTeam,
    /// <summary>
    /// One Business Central environment, audited only for the settings a user changes
    /// in the <em>customer's</em> tenant through this tool — not for the fetched cache
    /// the Refresh rewrites. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    ProjectEnvironment,

    /// <summary>
    /// A repository created in the organisation's connected GitHub organisation
    /// from New Workspace (issue #622). The one audited subject that is not a
    /// row of ours - it lives on GitHub - so its entries carry the repository's
    /// full name and no entity id.
    /// </summary>
    GitHubRepository,

    /// <summary>
    /// A passkey added to or removed from someone's account. Written by
    /// <c>PasskeyService</c> rather than the interceptor, because the passkey row
    /// also changes on every sign-in (its counter and last-used time) and none of
    /// that belongs in the log. Named by the passkey's nickname.
    /// </summary>
    Passkey,

    /// <summary>
    /// An organisation, audited only for its security policy (step-up tools and
    /// window, MCP opt-out, switched-off tools) and the SiteAdmin's storage quota
    /// override. Filed under the organisation itself. See <c>.design/auth-and-audit.md</c>.
    /// </summary>
    Organization,

    /// <summary>
    /// A Sealed link created or revoked. Written by <c>SealedLinkService</c> rather than
    /// the interceptor: the row changes on every open, and those are kept with the link
    /// for its sender instead. Named by the sender's note. See <c>.design/sealed.md</c>.
    /// </summary>
    SealedLink,
}

/// <summary>The kind of change captured by an audit row.</summary>
public enum AuditAction
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// One row per write to any of the audited tables. Filled in by the
/// <c>AuditInterceptor</c> on <c>SaveChanges</c> rather than by service code.
/// </summary>
public class AuditLogEntry
{
    public int Id { get; set; }

    /// <summary>UTC instant the change was committed.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// <c>"display_name &lt;email&gt;"</c> of the acting user. <c>"unknown"</c>
    /// when no signed-in user could be resolved (e.g. seed-time inserts).
    /// </summary>
    public string ChangedBy { get; set; } = string.Empty;

    /// <summary>FK to <c>users</c>. Null for changes that happen pre-login (seed, bootstrap).</summary>
    public int? ChangedByUserId { get; set; }
    public User? ChangedByUser { get; set; }

    /// <summary>Owning organisation. Null for changes that have no org context (seed bootstrap rows).</summary>
    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public AuditEntityType EntityType { get; set; }

    /// <summary>Primary key of the affected row in its own table.</summary>
    public int EntityId { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>
    /// What the affected row was <i>called</i> when the change happened —
    /// "CRONUS Standard" rather than <c>#4</c>. Captured by the interceptor from
    /// the entity itself (see <see cref="ValueObjects.AuditEntityName"/>), so it
    /// survives the row being renamed or deleted afterwards.
    ///
    /// <para>Null on three counts, all of which the reading side words around
    /// rather than papering over: rows written before this column existed (they
    /// fall back to the snapshot), singleton and join rows that have no name of
    /// their own, and anything whose name field was blank.</para>
    /// </summary>
    public string? EntityName { get; set; }

    /// <summary>
    /// JSON snapshot of the row's state *before* the change. <c>null</c> for
    /// <see cref="AuditAction.Created"/>, where there is no prior state.
    /// </summary>
    public string? SnapshotJson { get; set; }
}
