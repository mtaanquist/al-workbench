using System.Security.Claims;

namespace ALDevToolbox.Services.Palette;

/// <summary>
/// One kind of thing the command palette can find. Adding a source is one class
/// and one registration line in <c>Startup/PaletteRegistration.cs</c>; the
/// palette knows none of them by name. See <c>.design/command-palette.md</c>,
/// "Sources".
///
/// <para><b>A source returns candidates, never scores.</b>
/// <see cref="PaletteRanking"/> is the one place matching is decided, so
/// ranking cannot drift from source to source.</para>
///
/// <para><b>The fence.</b> A source is a new read path across the app, so it is
/// a new way to leak. Every source reads through the organisation query filter
/// (no <c>IgnoreQueryFilters()</c>, ever) <em>and</em> through the same
/// visibility rules its page uses — for anything hanging off a solution that
/// means <c>ProjectAccess.VisibleProjectPredicate</c> or
/// <c>VisibleReleasePredicate</c>. If the caller cannot open it, it is not
/// returned. Every source must pass the shared harness in
/// <c>ALDevToolbox.Tests/Palette/PaletteSourceVisibilityTestBase.cs</c>.</para>
/// </summary>
public interface IPaletteSource
{
    /// <summary>
    /// Stable id, used as the group's id on the wire (<c>solutions</c>,
    /// <c>environments</c>, ...). The browser keys nothing off it but the
    /// group heading; the row template is chosen by
    /// <see cref="PaletteCandidate.Kind"/>.
    /// </summary>
    string Id { get; }

    /// <summary>The group heading a user reads: "Solutions", "Environments", ... </summary>
    string Label { get; }

    /// <summary>
    /// Where this source's group sits among the others. Fixed, so a solution and
    /// its environments never shuffle against each other as the user types —
    /// use the constants on <see cref="PaletteGroupOrder"/>.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// The role check and feature gate the matching page already has. A caller
    /// who fails it is never asked to search, so a source that is off for this
    /// user costs nothing and leaks nothing.
    ///
    /// <para>Takes the principal rather than reading one out of an accessor so
    /// the gate can be exercised directly in a test; anything else a gate needs
    /// (the per-organisation tool toggles, say) is a constructor
    /// dependency.</para>
    /// </summary>
    Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct);

    /// <summary>
    /// Candidate rows for <paramref name="query"/>, at most
    /// <paramref name="limit"/> of them.
    ///
    /// <para>Over-returning is expected and cheap: ranking drops every candidate
    /// that does not match every term, so a source that pre-filters loosely in
    /// SQL should ask for more rows than it needs. Under-returning is the
    /// failure that shows — a row that never left the database cannot be
    /// ranked.</para>
    ///
    /// <para>Sources run one after another on the request's shared
    /// <c>DbContext</c> (see <see cref="PaletteSearchService"/>), so this is one
    /// <c>AsNoTracking()</c> query with a limit, projecting only the fields a row
    /// needs.</para>
    /// </summary>
    Task<IReadOnlyList<PaletteCandidate>> SearchAsync(PaletteQuery query, int limit, CancellationToken ct);
}

/// <summary>
/// The fixed order the groups appear in. Named constants rather than bare
/// numbers so a new source declares where it belongs rather than guessing a
/// number, and spaced so one can be slipped between two without renumbering.
/// </summary>
public static class PaletteGroupOrder
{
    public const int Solutions = 10;
    public const int Environments = 20;
    public const int Upgrades = 21;
    public const int Pipelines = 23;
    public const int ReleasePipelines = 26;
    public const int Releases = 30;
    public const int Recipes = 40;
    public const int Templates = 50;
    public const int Teams = 60;
    public const int People = 70;
    public const int Docs = 80;
}

/// <summary>
/// One row a source offers, before ranking has decided whether it matched.
///
/// <para>The searched fields are <see cref="Title"/>, <see cref="ShortName"/>,
/// <see cref="Subtitle"/> and <see cref="SearchOnly"/> — matching runs across
/// all four, so an environment whose subtitle is its solution's name is found by
/// typing the customer and the environment together (<c>con prod</c>).</para>
/// </summary>
/// <param name="Kind">
/// Which <c>&lt;template data-palette-row="..."&gt;</c> the browser clones for
/// this row: <c>solution</c>, <c>environment</c>, <c>upgrade</c>, <c>pipeline</c>,
/// <c>release-pipeline</c>, <c>release</c>, <c>recipe</c>, <c>template</c>, <c>team</c>.
/// An unknown kind falls back to the default template rather than failing.
/// </param>
/// <param name="Title">The name the user is typing — a solution's name, an environment's name.</param>
/// <param name="Subtitle">
/// The line under the title, and a searched field. Null when the row has
/// nothing useful to say beyond its title.
/// </param>
/// <param name="Href">
/// Where Enter lands. A relative, in-app path; the browser sets it on an anchor
/// so an unopenable row would be a dead end, which is why a row the caller
/// cannot open is never returned in the first place.
/// </param>
/// <param name="ShortName">
/// The abbreviation a team uses for this row, when it has one (a solution's
/// short name). Searched, and an <em>exact</em> match on it is what lifts a row
/// above every group as the palette's single top hit.
/// </param>
/// <param name="SearchOnly">
/// Text this row can be <em>found</em> by but that is never shown and never
/// leaves the server: <see cref="PaletteResultItem"/> does not carry it, so it
/// cannot reach the browser even by accident.
///
/// <para>It exists for the fields support types mid-call that a result must not
/// print back — a customer's Voice account number, their Business Central tenant
/// id. Ranking only matches what is in a searched field, so without this the
/// subtitle would have to carry the value to be findable by it. The row still
/// says <em>which</em> field matched in its subtitle, so nothing arrives
/// unexplained.</para>
///
/// <para><b>Not a place to put personal data.</b> A contact's name or company
/// may be matched on and named; a phone number or an email address is neither
/// searched nor stored here. See <c>.design/command-palette.md</c>.</para>
/// </param>
public sealed record PaletteCandidate(
    string Kind,
    string Title,
    string? Subtitle,
    string Href,
    string? ShortName = null,
    string? SearchOnly = null);
