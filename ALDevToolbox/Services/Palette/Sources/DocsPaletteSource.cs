using System.Security.Claims;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// The docs pages under <c>/docs</c>, found by their title, a line saying what
/// each is about, and their section headings - so <c>mcp</c> lands on the page
/// about connecting an assistant and <c>token</c> lands on the section about
/// access tokens. See <c>.design/command-palette.md</c>, "Sources".
///
/// <para><b>A hand-kept catalogue, not a table and not reflection.</b> There are
/// four docs pages and their headings live in Razor markup; reading them out of
/// the compiled components at startup would be cleverer than the problem.
/// Instead <see cref="Pages"/> lists them, and <c>DocsPaletteSourceTests</c>
/// renders every page and fails when a catalogued heading is missing from it or
/// a rendered section heading is missing from the catalogue - so the list
/// cannot quietly drift from the pages.</para>
///
/// <para><b>The fence.</b> Docs hang off nothing and are the same for every
/// organisation, so there is no database read and nothing to scope. The gate is
/// "signed in": two of the pages require it, the other two are public, and the
/// palette itself is only ever drawn for a signed-in person.</para>
/// </summary>
public sealed class DocsPaletteSource : IPaletteSource
{
    /// <summary>
    /// Every docs page, with the headings a reader might be looking for.
    /// <see cref="DocsPage.Title"/> is the page's own heading, and each
    /// <see cref="DocsHeading.Text"/> is a heading on it (without the "1."
    /// a numbered one carries) - the test named above holds both to what the
    /// page renders.
    /// </summary>
    public static readonly IReadOnlyList<DocsPage> Pages =
    [
        new("Connect an AI assistant", "Connecting an AI assistant", "/docs/mcp",
            "Connecting Claude, Cursor, Copilot or another assistant (MCP)",
            [
                new("Two ways to connect", "mcp-ways"),
                new("A personal access token", "mcp-token"),
                new("Signing in from the assistant", "mcp-consent"),
                new("Set up your assistant", "mcp-setup"),
                new("What it can do once it's connected", "mcp-does"),
                new("Taking access back", "mcp-revoke"),
                new("If it isn't working", "mcp-trouble"),
                new("Every tool it can use", "mcp-tools"),
            ]),
        new("Search or jump to", "Using the search box", "/docs/search",
            "How the search box finds customers, environments and pages",
            [
                new("Opening it", "search-open"),
                new("What it finds", "search-finds"),
                new("Choosing a result", "search-keys"),
                new("Doing things from it", "search-commands"),
                new("It never changes a customer's environment", "search-safe"),
                new("When it comes back empty", "search-nothing"),
            ]),
        new("Setting up the on-premises gateway", "Setting up the gateway", "/docs/gateway-setup",
            "Installing the on-premises gateway on a customer's Windows server, and its settings file",
            [
                new("What the gateway does, and what it does not", "gs-what"),
                new("What leaves your server", "gs-what-leaves"),
                new("What you need", "gs-requirements"),
                new("The service account", "gs-account"),
                new("Install and register it", "gs-install"),
                new("Download it and check the file", "gs-download"),
                new("The settings file", "gs-policy"),
                new("Permissions", "gs-permissions"),
                new("Before and after scripts", "gs-scripts"),
                new("Who may change the files", "gs-rights"),
                new("Cutting it off from your side", "gs-cut-off"),
                new("Updating it", "gs-update"),
                new("Removing it", "gs-uninstall"),
                new("When it is offline or revoked", "gs-offline"),
                new("Logs and event ids", "gs-logs"),
            ]),
        new("You're all set - here's what's next", "After you generate", "/docs/extensions-whats-next",
            "After Generate - opening the download and putting it in git",
            [
                new("Open the folder", "wn-open"),
                new("Save it with git", "wn-git"),
                new("Put it on your team's git server", "wn-push"),
                new("From here on", "wn-daily"),
                new("Stuck?", "wn-stuck"),
            ]),
    ];

    public string Id => "docs";

    public string Label => "Docs";

    public int Order => PaletteGroupOrder.Docs;

    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.Identity?.IsAuthenticated == true);
    }

    /// <summary>
    /// One row per page and one per heading; ranking decides which match. A
    /// page's row carries its topic line, so <c>mcp</c> finds a page whose title
    /// never says it; a heading's row says which page it is on, so
    /// <c>assistant token</c> finds the token section.
    /// </summary>
    public Task<IReadOnlyList<PaletteCandidate>> SearchAsync(PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable) return Task.FromResult<IReadOnlyList<PaletteCandidate>>([]);

        var candidates = new List<PaletteCandidate>();
        foreach (var page in Pages)
        {
            candidates.Add(new PaletteCandidate("doc", page.Title, page.Topic, page.Href));
            foreach (var heading in page.Headings)
            {
                candidates.Add(new PaletteCandidate(
                    "doc", heading.Text, "Help: " + page.Name, page.Href + "#" + heading.Anchor));
            }
        }

        IReadOnlyList<PaletteCandidate> ranked = PaletteRanking.Rank(query, candidates)
            .Take(limit)
            .Select(m => m.Candidate)
            .ToList();
        return Task.FromResult(ranked);
    }
}

/// <summary>One docs page in the palette's catalogue.</summary>
/// <param name="Title">The page's own heading, as it renders.</param>
/// <param name="Name">
/// A short name for the page, under each of its sections ("Help: After you generate") - the
/// title is too long, and one of them reads as a success message out of context.
/// </param>
/// <param name="Href">The page's route.</param>
/// <param name="Topic">One line saying what the page is about - the row's subtitle, and searched.</param>
/// <param name="Headings">The sections a reader might jump straight to.</param>
public sealed record DocsPage(
    string Title, string Name, string Href, string Topic, IReadOnlyList<DocsHeading> Headings);

/// <summary>A section heading on a docs page, and the id it carries.</summary>
public sealed record DocsHeading(string Text, string Anchor);
