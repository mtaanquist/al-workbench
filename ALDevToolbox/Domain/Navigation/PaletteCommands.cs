using ALDevToolbox.Domain.Tools;

namespace ALDevToolbox.Domain.Navigation;

/// <summary>
/// One thing the command palette can do, as opposed to one place it can go.
/// Exactly one of <see cref="Href"/> and <see cref="Action"/> is set: a
/// <em>navigate</em> command is an ordinary link to the page that owns a form,
/// an <em>act</em> command names one entry of <see cref="PaletteCommands.Actions"/>
/// that <c>wwwroot/command-palette.js</c> knows how to run.
/// </summary>
/// <param name="Subtitle">
/// The second line. For a navigate command it names where the row lands
/// ("Solutions", "Your account"), so a link reads differently from an action
/// that happens here ("This page", "Appearance").
/// </param>
/// <param name="Keywords">
/// Searched, never shown - the other words someone might type for this
/// ("tokens" for Repository access, "dark mode" for Dark theme).
/// </param>
/// <param name="OnOpen">
/// Shown before anything is typed. Most commands are not: a dozen rows would
/// push every page in "Go to" below the fold of a box people open to jump
/// somewhere. The rest are one word away.
/// </param>
public sealed record PaletteCommand(
    string Label,
    string Icon,
    string Subtitle,
    NavGate Gate = NavGate.SignedIn,
    ToolKey? Tool = null,
    string? Href = null,
    string? Action = null,
    string? Keywords = null,
    bool OnOpen = false)
{
    /// <summary>The text the palette's every-term-must-match rule runs over.</summary>
    public string SearchText => string.Join(' ',
        new[] { Label, Subtitle, Keywords }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// The palette's Commands group. Rendered into the page by
/// <c>CommandPalette.razor</c> and filtered in the browser, exactly like
/// <see cref="NavDestinations"/> - no endpoint, no server round trip. See
/// <c>.design/command-palette.md</c>, "Commands".
///
/// <para><b>No command writes to a customer's tenant.</b> A navigate command
/// only opens a page, and every write that page can make still sits behind the
/// page's own confirm. An act command is one of <see cref="Actions"/>, which
/// is the whole list the script will run: none of them leaves the browser
/// except Sign out, which submits the shell's own sign-out form.
/// <c>PaletteCommandsTests</c> fails when a command names an action outside
/// that list or links to a page that does not exist.</para>
/// </summary>
public static class PaletteCommands
{
    /// <summary>Follow the operating system's light or dark setting.</summary>
    public const string ThemeSystem = "theme:system";
    public const string ThemeLight = "theme:light";
    public const string ThemeDark = "theme:dark";
    /// <summary>Copies the address of the page the palette was opened on.</summary>
    public const string CopyLink = "copy-link";
    /// <summary>Submits the top bar's sign-out form, antiforgery token and all.</summary>
    public const string SignOut = "sign-out";
    /// <summary>
    /// Presses the page's own Refresh button, which a page marks with
    /// <c>data-page-refresh</c>. Offered only on a page that has one - the
    /// script looks when the palette opens.
    /// </summary>
    public const string Refresh = "refresh";

    /// <summary>
    /// Every action the script will run, and it refuses any other. Adding one
    /// means adding its handler to <c>command-palette.js</c> and a line to the
    /// design doc - and it may not write to a customer's tenant.
    /// </summary>
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
    {
        ThemeLight, ThemeDark, ThemeSystem, CopyLink, SignOut, Refresh,
    };

    /// <summary>In display order: make something, this page, how it looks, you, admin, and Sign out last.</summary>
    public static readonly IReadOnlyList<PaletteCommand> All =
    [
        // ---- Create: each opens the page that owns the form ----
        new("New solution", "plus", "Solutions", Tool: ToolKey.Projects, Href: "/solutions/new",
            Keywords: "create customer add", OnOpen: true),
        new("New workspace", "folder-plus", "Templates", Tool: ToolKey.Templates, Href: "/templates/workspace",
            Keywords: "create generate"),
        new("New extension", "file-plus", "Templates", Tool: ToolKey.Templates, Href: "/templates/extension",
            Keywords: "create generate app"),
        new("Suggest a recipe", "square-code", "Cookbook", Tool: ToolKey.Cookbook, Href: "/cookbook/suggest",
            Keywords: "new recipe suggestion create"),

        // ---- This page ----
        new("Copy link to this page", "link", "This page", Action: CopyLink,
            Keywords: "url address share", OnOpen: true),
        // "Refresh", as the button it presses says. It reads Business Central
        // again; it writes nothing there.
        new("Refresh", "refresh-cw", "This page", Action: Refresh,
            Keywords: "reload business central", OnOpen: true),

        // ---- Appearance: the same three the top bar's toggle offers ----
        new("Light theme", "sun", "Appearance", Action: ThemeLight, Keywords: "mode"),
        new("Dark theme", "moon", "Appearance", Action: ThemeDark, Keywords: "mode"),
        new("Follow system theme", "monitor", "Appearance", Action: ThemeSystem, Keywords: "mode auto"),

        // ---- You ----
        new("Repository access", "key-round", "Your account", Href: "/account/repos",
            Keywords: "repository tokens github azure devops"),

        // ---- Admin ----
        new("Import a Business Central release", "upload", "Object Explorer", Gate: NavGate.ContentAuthor,
            Href: "/admin/object-explorer/new", Keywords: "artifacts dvd symbols"),
        new("Business Central app registration", "plug", "Administration", Gate: NavGate.PerOrgAdmin,
            Href: "/admin/administration/business-central", Keywords: "entra client secret"),

        // Last, and never before anything is typed: one Enter on it ends the
        // session and whatever form was half filled in.
        new("Sign out", "log-out", "Your account", Action: SignOut, Keywords: "log out logout"),
    ];

    /// <summary>The commands <paramref name="viewer"/> may use, in display order.</summary>
    public static IReadOnlyList<PaletteCommand> VisibleTo(NavViewer viewer) =>
        All.Where(c => NavDestinations.Passes(c.Gate, c.Tool, viewer)).ToList();
}
