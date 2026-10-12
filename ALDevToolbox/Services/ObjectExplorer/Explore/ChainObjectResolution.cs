using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Resolves an object (or a member symbol on one) by name across the
/// <em>visible release chain</em> seeded at a release — the project /
/// third-party-on-parent case where a referenced base object (Customer,
/// Sales Header, …) physically lives in an ancestor Release, not the seed.
///
/// <para>The seed-only object lookups in <see cref="ReferenceResolver"/>,
/// <see cref="SourceViewerService"/> and the MCP find-references tools used to
/// stop at <c>Module.ReleaseId == seed</c>, so a click on a base table from a
/// project Release resolved to nothing. This walks the same recursive
/// ancestry + app-id shadowing CTE the find-references <em>queries</em> already
/// use (<see cref="ReleaseAncestrySql.WinningModules"/>): a child Release sees
/// an ancestor's module only when it doesn't ship its own copy, and the
/// closest-depth match wins on a name collision across apps.</para>
///
/// <para><b>Tenant fence.</b> These methods run raw SQL and so bypass the EF
/// query filter — the same property the find-references queries have. Every
/// caller seeds from a release id it already obtained through an org-filtered
/// EF read (the clicked file's release, or <c>ResolveReleaseAsync</c>), and a
/// parent chain never crosses an org boundary: the import reads the chosen
/// parent back through the org-filtered set and refuses one it cannot see, and
/// the chain SQL's parent step follows only a release in the seed's own
/// organisation as a backstop, so the walk stays in-tenant. The dependency
/// links a pipeline build adds (#901) keep that argument: the build writes
/// them inside its own org scope, linking releases it read or created there,
/// and the chain SQL does not take the link row's
/// word for it anyway - it follows a link only to a release in the seed's own
/// organisation (<see cref="ReleaseAncestrySql.Chain"/>). Don't call these with
/// a release id that hasn't been org-validated.</para>
/// </summary>
internal static class ChainObjectResolution
{
    /// <summary>
    /// Resolves an object by <paramref name="name"/> (optionally narrowed to
    /// <paramref name="kind"/> and/or <paramref name="objectId"/>) across the
    /// visible chain, returning the closest-depth hit or <see langword="null"/>.
    /// <para><paramref name="kindHint"/> is a soft preference rather than the
    /// hard <paramref name="kind"/> filter: the kind the call site's context
    /// suggests (a <c>Database::</c> / <c>Codeunit::</c> prefix, a
    /// <c>Record X</c> declaration, a <c>SourceTable =</c> property). Matching
    /// candidates sort first; everything else falls back to the deliberate
    /// kind order in <see cref="ALDevToolbox.Services.Al.AlKindKeywords.KindPriority"/>
    /// — tables before codeunits, and every base object ahead of the extension
    /// kind that extends it — then the closest Release wins. Ranking used to be
    /// alphabetical by kind, which put a codeunit ahead of a same-named table
    /// (issue #712).</para>
    /// </summary>
    public static async Task<ChainObjectHit?> ResolveObjectAsync(
        AppDbContext db, int seedReleaseId, string name, string? kind, int? objectId, CancellationToken ct,
        string? kindHint = null)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT
                o.id             AS "Id",
                o.source_file_id AS "SourceFileId",
                o.line_number    AS "LineNumber",
                o.kind           AS "Kind",
                o.name           AS "Name",
                o.object_id      AS "ObjectId",
                m.app_id         AS "AppId",
                m.id             AS "ModuleId",
                m.name           AS "ModuleName"
            FROM oe_module_objects o
            JOIN winning   w  ON w.id  = o.module_id
            JOIN oe_modules m  ON m.id  = o.module_id
            JOIN chain     ch ON ch.id = m.release_id
            WHERE lower(o.name) = lower({1})
              AND ({2}::text IS NULL OR lower(o.kind) = lower({2}::text))
              AND ({3}::int  IS NULL OR o.object_id   = {3}::int)
            -- Prefer a navigable object (one with source) so go-to-definition
            -- lands somewhere; then the kind the click's context asked for;
            -- then the deliberate kind order (base objects ahead of their
            -- extensions); then the closest Release and a stable id tiebreak.
            ORDER BY (o.source_file_id IS NULL),
                     ({4}::text IS NOT NULL AND lower(o.kind) = lower({4}::text)) DESC,
                     COALESCE(array_position({5}::text[], lower(o.kind)), array_length({5}::text[], 1) + 1),
                     ch.depth ASC, o.id ASC
            LIMIT 1
            """;

        var hits = await db.Database
            .SqlQueryRaw<ChainObjectHit>(
                sql,
                seedReleaseId,
                name,
                (object?)kind ?? DBNull.Value,
                (object?)objectId ?? DBNull.Value,
                (object?)ALDevToolbox.Services.Al.AlKindKeywords.MapKeywordToKind(kindHint) ?? DBNull.Value,
                ALDevToolbox.Services.Al.AlKindKeywords.KindPriority)
            .ToListAsync(ct);
        return hits.FirstOrDefault();
    }

    /// <summary>
    /// Resolves a member symbol (procedure / field / trigger) named
    /// <paramref name="memberName"/> on the object named
    /// <paramref name="ownerName"/> across the visible chain, returning the
    /// symbol id or <see langword="null"/>.
    /// </summary>
    public static async Task<long?> ResolveMemberSymbolIdAsync(
        AppDbContext db, int seedReleaseId, string ownerName, string memberName, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT s.id AS "Id"
            FROM oe_module_symbols s
            JOIN oe_module_objects o  ON o.id  = s.object_id
            JOIN winning           w  ON w.id  = o.module_id
            JOIN oe_modules        m  ON m.id  = o.module_id
            JOIN chain             ch ON ch.id = m.release_id
            WHERE lower(o.name) = lower({1})
              AND lower(s.name) = lower({2})
            ORDER BY s.kind ASC, ch.depth ASC, s.id ASC
            LIMIT 1
            """;

        var hits = await db.Database
            .SqlQueryRaw<ChainIdRow>(sql, seedReleaseId, ownerName, memberName)
            .ToListAsync(ct);
        return hits.Count > 0 ? hits[0].Id : null;
    }
}

/// <summary>One object resolved across the release chain. Column aliases in
/// <see cref="ChainObjectResolution"/>'s SQL map to these property names.</summary>
public sealed record ChainObjectHit(
    long Id,
    long? SourceFileId,
    int LineNumber,
    string Kind,
    string Name,
    int? ObjectId,
    Guid AppId,
    int ModuleId,
    string ModuleName);

/// <summary>Single-id projection for the member-symbol resolution query.</summary>
public sealed record ChainIdRow(long Id);
