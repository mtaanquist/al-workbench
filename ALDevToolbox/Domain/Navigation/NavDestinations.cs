using ALDevToolbox.Domain.Tools;

namespace ALDevToolbox.Domain.Navigation;

/// <summary>
/// The facts a destination's gate is decided against: who is signed in, what
/// their org looks like, and which tools are switched on for them. Pure data,
/// so the same list can be filtered from a Razor component, a test, or
/// anything else that can answer these questions.
/// </summary>
/// <param name="VisibleTools">
/// The tools that are enabled site-wide <em>and</em> not switched off for this
/// organisation - i.e. the answer <c>NavMenu.ToolVisible</c> gives.
/// </param>
/// <param name="CanUseEnvironmentOps">
/// Whether this person may run Business Central update actions anywhere. It is
/// a per-team flag that deliberately never enters the sign-in claims, so the
/// caller has to have asked <c>ProjectAccess</c> for it.
/// </param>
public sealed record NavViewer(
    bool IsAuthenticated,
    bool IsAdmin,
    bool IsEditor,
    bool IsSiteAdmin,
    bool IsSystemOrganization,
    bool SingleTenantMode,
    bool CanUseEnvironmentOps,
    IReadOnlySet<ToolKey> VisibleTools)
{
    /// <summary>
    /// Mirrors <c>NavMenu</c>'s <c>showPerOrgContent</c>: in single-tenant mode
    /// the lone org IS the system org, so it owns its own templates and
    /// Administration and the per-org pages have to come back.
    /// </summary>
    public bool ShowsPerOrgContent => !IsSystemOrganization || SingleTenantMode;
}

/// <summary>
/// Who may reach a destination. Each member names one of the visibility rules
/// the sidebar already expresses in nested <c>AuthorizeView</c>s and flags;
/// <see cref="NavDestinations.IsVisible"/> is the one place they are evaluated.
/// </summary>
public enum NavGate
{
    /// <summary>Anyone who can see the shell, signed in or not.</summary>
    Everyone,

    /// <summary>Any signed-in person, whatever their role.</summary>
    SignedIn,

    /// <summary>Whoever may run Business Central update actions (Upgrades).</summary>
    EnvironmentOps,

    /// <summary>An Admin or an Editor - the content-authoring pages under /admin.</summary>
    ContentAuthor,

    /// <summary>A content author, where the organisation owns its own content.</summary>
    PerOrgContentAuthor,

    /// <summary>An organisation Admin (the Dashboard).</summary>
    OrgAdmin,

    /// <summary>An organisation Admin, where the organisation owns its own settings.</summary>
    PerOrgAdmin,

    /// <summary>
    /// An organisation Admin who is <em>not</em> a SiteAdmin. The per-org audit
    /// log; a SiteAdmin gets the cross-org one instead.
    /// </summary>
    OrgAdminOnly,

    /// <summary>A SiteAdmin - the cross-org console.</summary>
    SiteAdmin,

    /// <summary>
    /// Either pipeline tool on: the Pipelines dashboard, which covers build and
    /// deployment pipelines and so stays while either toggle does (#955). A gate
    /// rather than a <see cref="NavDestination.Tool"/>, which names one tool.
    /// </summary>
    AnyPipelineTool,
}

/// <summary>
/// One place a person can go. <see cref="Parent"/> is the entry this one sits
/// under in the sidebar, which the palette shows as the row's second line so
/// "Workspace" is not offered on its own.
/// </summary>
/// <param name="Group">The sidebar section, used as the second line when there is no parent.</param>
/// <param name="Tool">The toggleable tool this page belongs to, if any.</param>
/// <param name="SystemOrgLabel">What the sidebar calls this inside the singleton system org, when that differs.</param>
public sealed record NavDestination(
    string Label,
    string Href,
    string Icon,
    string Group,
    NavGate Gate = NavGate.Everyone,
    ToolKey? Tool = null,
    string? Parent = null,
    string? SystemOrgLabel = null)
{
    /// <summary>What to call this destination to <paramref name="viewer"/>.</summary>
    public string LabelFor(NavViewer viewer) =>
        viewer.IsSystemOrganization && SystemOrgLabel is not null ? SystemOrgLabel : Label;

    /// <summary>
    /// The second line: where this page lives. Suppressed when it would only
    /// repeat the label back (Home, Account).
    /// </summary>
    public string SubtitleFor(NavViewer viewer)
    {
        var subtitle = Parent ?? Group;
        return string.Equals(subtitle, LabelFor(viewer), StringComparison.Ordinal) ? string.Empty : subtitle;
    }
}

/// <summary>
/// Every page the sidebar can offer, in one list, so the sidebar and the
/// command palette cannot drift. See <c>.design/command-palette.md</c>.
///
/// <para><b>This is the list, not the renderer.</b> <c>NavMenu.razor</c> still
/// owns its own markup - its groups collapse, nest and carry active states that
/// a flat list has no room for. What this guarantees is that the two agree on
/// <em>which pages exist and who may see them</em>:
/// <c>NavCatalogueParityTests</c> fails if the sidebar links somewhere this
/// list does not, or the other way round.</para>
///
/// <para>Gates are copied from the sidebar deliberately rather than derived: a
/// destination the palette offers and the page then refuses is worse than one
/// it never offered.</para>
/// </summary>
public static class NavDestinations
{
    /// <summary>
    /// In sidebar order, so the palette's "Go to" list reads like the sidebar
    /// the user already knows.
    /// </summary>
    public static readonly IReadOnlyList<NavDestination> All =
    [
        new("Home", "/", "house", "Home"),

        // ---- Build ----
        new("Templates", "/templates", "folder-plus", "Build", Tool: ToolKey.Templates),
        new("Workspace", "/templates/workspace", "folder-plus", "Build",
            Tool: ToolKey.Templates, Parent: "Templates"),
        new("Extension", "/templates/extension", "file-plus", "Build",
            Tool: ToolKey.Templates, Parent: "Templates"),
        new("Cookbook", "/cookbook", "square-code", "Build", Tool: ToolKey.Cookbook),
        new("Object Explorer", "/object-explorer", "package", "Build", Tool: ToolKey.ObjectExplorer),

        // ---- Work with text ----
        new("Translator", "/translator", "languages", "Work with text", Tool: ToolKey.Translator),
        new("Diff", "/diff", "git-compare", "Work with text", Tool: ToolKey.Compare),
        new("Piper", "/piper", "git-merge", "Work with text", Tool: ToolKey.Piper),
        new("Sealed", "/sealed", "lock", "Work with text", Tool: ToolKey.Sealed),

        // ---- Deliver ----
        new("Solutions", "/solutions", "folder-git-2", "Deliver", Tool: ToolKey.Projects),
        new("Environments", "/environments", "server", "Deliver",
            Tool: ToolKey.Projects, Parent: "Solutions"),
        // Upgrades is its own grant and its own route: the sidebar shows it
        // under Solutions when that tool is on and on its own when it is off,
        // so the net rule is the grant alone.
        new("Upgrades", "/upgrades", "calendar", "Deliver", Gate: NavGate.EnvironmentOps),
        new("Teams", "/teams", "users", "Deliver", Gate: NavGate.SignedIn),
        // The sidebar's Pipelines parent is the dashboard over its two children (#955);
        // the parent's name rides along as their second line.
        new("Pipelines", "/pipelines", "workflow", "Deliver", Gate: NavGate.AnyPipelineTool),
        new("Builds", "/pipelines/builds", "rocket", "Deliver", Tool: ToolKey.Pipelines, Parent: "Pipelines"),
        new("Deployments", "/pipelines/deployments", "send", "Deliver", Tool: ToolKey.Releases, Parent: "Pipelines"),

        // ---- Connect an assistant ----
        new("MCP", "/tools/mcp", "bot", "Connect an assistant",
            Gate: NavGate.SignedIn, Tool: ToolKey.Mcp),

        // ---- Admin ----
        new("Dashboard", "/admin", "layout-dashboard", "Admin", Gate: NavGate.OrgAdmin),
        new("Templates", "/admin/templates", "layers", "Admin", Gate: NavGate.ContentAuthor),
        new("Defaults", "/admin/templates/defaults", "layers", "Admin",
            Gate: NavGate.PerOrgContentAuthor, Parent: "Templates"),
        new("Always-included files", "/admin/templates/files", "layers", "Admin",
            Gate: NavGate.PerOrgContentAuthor, Parent: "Templates"),
        new("Workspace settings", "/admin/templates/workspace", "layers", "Admin",
            Gate: NavGate.PerOrgContentAuthor, Parent: "Templates"),
        new("Modules", "/admin/modules", "package", "Admin", Gate: NavGate.ContentAuthor),
        new("Customer modules", "/admin/customer-modules", "package-plus", "Admin", Gate: NavGate.ContentAuthor),
        new("Catalogue", "/admin/catalog", "book-open", "Admin", Gate: NavGate.ContentAuthor),
        new("Cookbook", "/admin/cookbook", "square-code", "Admin", Gate: NavGate.ContentAuthor),
        new("App versions", "/admin/application-versions", "tag", "Admin", Gate: NavGate.ContentAuthor),
        new("Object Explorer", "/admin/object-explorer", "file-code", "Admin", Gate: NavGate.ContentAuthor),
        new("Translation memory", "/admin/translation-memory", "languages", "Admin", Gate: NavGate.ContentAuthor),
        new("Administration", "/admin/administration", "settings", "Admin", Gate: NavGate.PerOrgAdmin),
        new("Audit log", "/admin/audit", "history", "Admin", Gate: NavGate.OrgAdminOnly),

        // ---- Site administration ----
        new("All users", "/site-admin/users", "users-round", "Site administration",
            Gate: NavGate.SiteAdmin, SystemOrgLabel: "Users"),
        new("Audit log", "/site-admin/audit", "history", "Site administration", Gate: NavGate.SiteAdmin),
        new("Backup & storage", "/site-admin/backup-storage", "database-backup", "Site administration",
            Gate: NavGate.SiteAdmin),
        new("Connections", "/site-admin/connections", "plug", "Site administration", Gate: NavGate.SiteAdmin),
        new("Email delivery", "/site-admin/email", "mail", "Site administration", Gate: NavGate.SiteAdmin),
        new("Workers", "/site-admin/workers", "monitor", "Site administration", Gate: NavGate.SiteAdmin),
        new("Settings", "/site-admin/settings", "server-cog", "Site administration", Gate: NavGate.SiteAdmin),

        // ---- Reachable from the shell, but not from the sidebar ----
        new("Account", "/account", "user", "Account", Gate: NavGate.SignedIn),
        // "How to" rather than "Connect an assistant": the sidebar already has an
        // MCP entry that is where you connect one, and two rows carrying the same
        // three words in a different order say nothing about which is which.
        new("How to connect an assistant", "/docs/mcp", "book-open", "Docs"),
        // The palette's own docs page. In the list as well as in the foot,
        // because the foot is hidden at phone width - where the palette is the
        // only way to navigate at all, so it had better be able to explain
        // itself. Signed-in: the palette never renders for anyone else.
        new("How search works", "/docs/search", "search", "Docs", Gate: NavGate.SignedIn),
    ];

    /// <summary>
    /// The destinations <paramref name="viewer"/> may open, in sidebar order.
    /// </summary>
    public static IReadOnlyList<NavDestination> VisibleTo(NavViewer viewer) =>
        All.Where(d => IsVisible(d, viewer)).ToList();

    /// <summary>
    /// One destination's gate. The tool toggle is checked first because it
    /// outranks every role: a tool switched off site-wide is gone for everyone,
    /// admins included.
    /// </summary>
    public static bool IsVisible(NavDestination destination, NavViewer viewer) =>
        Passes(destination.Gate, destination.Tool, viewer);

    /// <summary>
    /// A gate and an optional tool, evaluated against <paramref name="viewer"/>.
    /// Shared with <see cref="PaletteCommands"/>, so a command is gated by exactly
    /// the rules the sidebar's pages are.
    /// </summary>
    public static bool Passes(NavGate gate, ToolKey? tool, NavViewer viewer)
    {
        if (tool is { } key && !viewer.VisibleTools.Contains(key))
        {
            return false;
        }

        // Both admin sections live inside the sidebar's AuthorizeView
        // Roles="Admin,Editor", so a SiteAdmin who is somehow neither never
        // sees the cross-org section there either. Match that, rather than
        // offering a door the sidebar does not have.
        var contentAuthor = viewer.IsAdmin || viewer.IsEditor;

        return gate switch
        {
            NavGate.Everyone => true,
            NavGate.SignedIn => viewer.IsAuthenticated,
            NavGate.EnvironmentOps => viewer.IsAuthenticated && viewer.CanUseEnvironmentOps,
            NavGate.ContentAuthor => contentAuthor,
            NavGate.PerOrgContentAuthor => contentAuthor && viewer.ShowsPerOrgContent,
            NavGate.OrgAdmin => viewer.IsAdmin,
            NavGate.PerOrgAdmin => viewer.IsAdmin && viewer.ShowsPerOrgContent,
            NavGate.OrgAdminOnly => viewer.IsAdmin && !viewer.IsSiteAdmin,
            NavGate.SiteAdmin => contentAuthor && viewer.IsSiteAdmin,
            NavGate.AnyPipelineTool => viewer.VisibleTools.Contains(ToolKey.Pipelines)
                || viewer.VisibleTools.Contains(ToolKey.Releases),
            _ => false,
        };
    }
}
