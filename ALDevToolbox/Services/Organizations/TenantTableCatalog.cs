namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Static catalogue of the database tables that carry an
/// <c>organization_id</c>. Used by both <see cref="DatabaseUsageService"/>
/// (to prorate per-org disk usage) and the per-tenant backup service (to
/// know which tables to dump and restore).
///
/// Every tenanted table must appear in exactly one of
/// <see cref="ContentTables"/>, <see cref="AuthAndAuditTables"/> or
/// <see cref="DeliberatelyExcluded"/>. That is enforced by the model-driven
/// tests in <c>ALDevToolbox.Tests/Schema/TenantTableCatalogTests.cs</c>, which
/// also check that <see cref="ContentTables"/> is in valid FK order and that
/// nothing cascade-deleted by a restore is left out of the re-insert phase
/// (#665 — five tables were silently wiped by every per-tenant restore
/// because their parent was deleted and they were never restored).
///
/// Deliberately absent: <c>oe_file_contents</c>, the content-addressed source
/// store. It has no <c>organization_id</c> (it's cross-tenant shared) so it
/// can't be prorated by row share or filtered by org. Its logical size is
/// attributed via <c>oe_releases.source_content_length</c> in
/// <see cref="DatabaseUsageService"/>, and the per-tenant backup captures the
/// org's referenced blobs explicitly (see <c>PerTenantBackupService</c>). Do
/// not add it here.
/// </summary>
internal static class TenantTableCatalog
{
    /// <summary>
    /// Tables containing tenant content — the per-tenant backup payload. In
    /// insert order (parents before children); reverse for deletes.
    ///
    /// Order is load-bearing: the restore inserts in this order, so a table
    /// must follow every table it references — including <c>SET NULL</c>
    /// references, whose value still has to resolve at insert time.
    /// </summary>
    public static readonly IReadOnlyList<string> ContentTables =
    [
        "application_versions",
        "well_known_dependencies",
        "runtime_templates",
        "modules",
        "runtime_template_default_modules",
        // Only references runtime_templates, so it sits with the sibling join
        // above rather than down with runtime_template_included_files, which
        // has to wait for organization_files.
        "runtime_template_root_folders",
        "module_dependencies",
        "module_extension_folders",
        "module_extension_files",
        "workspace_extensions",
        "workspace_extension_folders",
        "workspace_extension_files",
        "workspace_extension_dependencies",
        "organization_settings",
        "organization_assets",
        "organization_files",
        "github_repository_standard_files",
        "organization_email_domains",
        "runtime_template_included_files",
        "teams",
        "team_members",
        "oe_artifact_versions",
        "oe_releases",
        "oe_release_dependencies",
        "oe_import_jobs",
        "oe_project_build_results",
        "oe_modules",
        "oe_module_files",
        "oe_module_objects",
        "oe_module_symbols",
        "oe_module_variables",
        "oe_module_references",
        "oe_module_system_references",
        "oe_module_translations",
        "oe_projects",
        "oe_project_repositories",
        // What GitHub's push and pull_request webhooks said about each solution
        // repository's branches (#963). Not recomputable - nothing polls GitHub
        // for it - so restored with the repositories they hang off.
        "oe_repository_branch_heads",
        "oe_repository_merged_pull_requests",
        "oe_project_environments",
        // Planned upgrades (#984): the header before its lines and before the actions
        // that name it, so the restore inserts parents first.
        "oe_environment_upgrades",
        "oe_environment_upgrade_lines",
        "oe_environment_upgrade_actions",
        "oe_pipelines",
        "oe_release_pipelines",
        "oe_project_symbols",
        "oe_project_teams",
        "oe_project_contacts",
        "oe_project_people",
        // Who follows which solution (#1048). Restored with the solutions, like the
        // People list it extends.
        "oe_project_followers",
        "oe_project_integrations",
        "customer_modules",
        "oe_project_modules",
        "oe_environment_apps",
        "oe_project_builds",
        "oe_project_build_repo_commits",
        "oe_project_build_commits",
        "oe_project_build_artifacts",
        "oe_project_build_logs",
        "oe_project_build_diagnostics",
        "oe_project_deliveries",
        "oe_project_delivery_results",
        "recipes",
        "recipe_files",
        "recipe_downloads",
        "recipe_suggestions",
        "recipe_suggestion_files",
        "translation_memory",
        "translation_memory_votes",
        // A cache of what GitHub said, but also where a person's "not this one"
        // lives (ignored_at), which no sweep can work out again. Its only parent
        // is organizations, so it can sit last.
        "github_repository_candidates",
        // Recomputable by a rescan, but restored all the same: it hangs off
        // oe_releases, so a restore that deleted the releases would take these
        // with them and put nothing back.
        "github_repository_drift",
        // Which Business Central versions have already been offered to each
        // repository (#1104). Restorable content: without it the nightly run would
        // open again every update pull request somebody had closed. Its only
        // parent is organizations.
        "github_update_pull_requests",
        // Which repository file each learned pair last came from, and the
        // version of it already read. Restorable content: without it a restored
        // organisation would re-read every translation file once, which costs
        // calls and teaches nothing new.
        "translation_memory_sources",
    ];

    /// <summary>
    /// Tenanted tables that carry auth state or forensic history. Counted
    /// toward per-org disk usage so SiteAdmin sees the real footprint, but
    /// excluded from per-tenant backup/restore: replaying users, credentials
    /// or audit rows from a snapshot would tangle login state and lose
    /// evidence.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthAndAuditTables =
    [
        "users",
        "user_passkeys",
        "user_recovery_codes",
        "user_totp_secrets",
        "user_external_logins",
        "invites",
        "signup_requests",
        "password_reset_tokens",
        "personal_access_tokens",
        "user_repository_tokens",
        // Per-person notification choices. Tied to users, which are not
        // restored either, so a restore keeps the choices people have now.
        "user_notification_settings",
        "oauth_consents",
        "audit_log",
    ];

    /// <summary>
    /// Tenanted tables that belong in neither list, with the reason. These are
    /// neither backed up nor counted toward per-org usage. Keep the reason
    /// current — the schema test reads this dictionary as the record of a
    /// decision, not as a suppression list.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeliberatelyExcluded =
        new Dictionary<string, string>
        {
            ["per_tenant_backups"] = "The snapshot inventory itself. Restoring it would resurrect rows for snapshots that have since been pruned, pointing at files that no longer exist on disk. Never cascade-deleted by a restore: its only parent is organizations.",
            ["organization_usage_snapshots"] = "Derived storage figures, recomputed on a schedule by UsageSnapshotScheduler. Backing them up would restore stale numbers over fresh ones. Never cascade-deleted by a restore: its only parent is organizations.",
            ["email_outbox"] = "Transactional email in flight. Restoring it would re-send mail the recipient either already got or no longer needs, carrying reset and invite links that expired long before the restore. Rows are pruned within a day of sending anyway, and its organization_id is a label for the SiteAdmin list rather than a tenant link - there is no foreign key to cascade. See issue #790.",
            ["notification_digest_items"] = "Notifications waiting for someone's next daily or weekly digest. Restoring them would send old news again in the next digest, and they are deleted as soon as that digest goes out (or after 30 days). A restore also removes the ones about a solution, by cascade from oe_projects, which is the same outcome. See .design/notifications.md.",
            ["user_notifications"] = "The in-app list of notifications. Restoring it would bring back old news as unread, it is tied to users (which are not restored), and rows are deleted after 30 days anyway. A restore also removes the ones about a solution, by cascade from oe_projects, which is the same outcome. See .design/notifications.md.",
        };

    /// <summary>All tables that carry an <c>organization_id</c>.</summary>
    public static IEnumerable<string> AllTenantedTables =>
        ContentTables.Concat(AuthAndAuditTables);

    /// <summary>
    /// Auth-adjacent tables that link to <c>organization_id</c> indirectly via
    /// <c>users.id</c>. Sized by joining through <c>users</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TablesLinkedThroughUser =
        new Dictionary<string, string>
        {
            ["user_passkeys"] = "user_id",
            ["user_recovery_codes"] = "user_id",
            ["user_totp_secrets"] = "user_id",
            ["user_external_logins"] = "user_id",
            ["password_reset_tokens"] = "user_id",
        };

    /// <summary>
    /// Object Explorer fact tables whose per-org row share is *estimated* from
    /// the owning org's share of <c>oe_modules</c> rather than counted.
    ///
    /// These five are the largest tables in the schema by a wide margin — a
    /// fully-loaded catalogue runs to millions of objects and tens of millions
    /// of references — and every row of every one of them hangs off exactly one
    /// <c>oe_modules</c> row. Counting them for the usage sweep meant a full
    /// index scan of ~100M tuples on every pass, which also evicted the Object
    /// Explorer's hot pages from the buffer cache (#684). The usage figure feeds
    /// a storage bar and a quota guard, not billing, and the whole computation
    /// is already an approximation, so a module-share estimate is accurate
    /// enough: modules are the unit orgs actually import, and their fact rows
    /// scale with them.
    /// </summary>
    public static readonly IReadOnlySet<string> ModuleShareEstimatedTables =
        new HashSet<string>
        {
            "oe_module_objects",
            "oe_module_symbols",
            "oe_module_variables",
            "oe_module_references",
            "oe_module_system_references",
        };

    /// <summary>
    /// The small, org-scoped table whose per-org row share stands in for the
    /// share of every table in <see cref="ModuleShareEstimatedTables"/>.
    /// </summary>
    public const string ModuleShareBasisTable = "oe_modules";

    /// <summary>
    /// Tables whose rows are scoped by <c>organization_id</c> directly —
    /// everything catalogued except the handful that reach the org through
    /// <c>users</c>. Derived rather than hand-listed so it can't drift out of
    /// step with the two lists above.
    /// </summary>
    public static readonly IReadOnlySet<string> TablesWithDirectOrgColumn =
        ContentTables.Concat(AuthAndAuditTables)
            .Where(t => !TablesLinkedThroughUser.ContainsKey(t))
            .ToHashSet();
}
