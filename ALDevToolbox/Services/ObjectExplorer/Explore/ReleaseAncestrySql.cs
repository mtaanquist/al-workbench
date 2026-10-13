namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Shared recursive-CTE fragments for the release-ancestry chain. Every
/// find-references / dependency / interface-implementer query walks the same
/// chain and resolves each AppId to the module at the smallest depth (closest
/// to the current release) — that "winning" selection is the shadowing rule: a
/// child release sees an ancestor's module only when it doesn't ship its own
/// copy. Defining the chain + winning CTEs once keeps the queries — split
/// across <see cref="ObjectExplorerService"/> (interface-implementer outline)
/// and <see cref="ReferenceQueryService"/> (find-references / dependencies) —
/// from drifting. <c>{0}</c> is the seed release id, and each query appends
/// its own SELECT tail. <see cref="WinningModules"/> omits <c>m.name</c>;
/// <see cref="WinningModulesWithName"/> includes it for queries that report
/// the module.
///
/// <para><b>Two seeds (#901).</b> The walk starts from the release itself and
/// from the vendor releases it links in <c>oe_release_dependencies</c>, and
/// follows the ordinary parent pointer from both. <c>depth</c> is a rank, not a
/// hop count: a dependency link costs one and a parent step costs two, so the
/// order is the release (0), its dependency releases (1), its own parent (2),
/// a dependency's parent (3), and so on. That puts a vendor's objects between
/// ours and Microsoft's, and when a vendor release was ingested on top of a
/// different Business Central version than this release's, our own parent
/// still wins over the vendor's. For a release with no links the ranks are
/// 0, 2, 4, ... so every ordering is what it was. A release reachable by two
/// paths keeps its lowest rank (<c>chain</c> has one row per release). Links
/// are followed one level only: the recursive step walks parent pointers,
/// never a dependency release's own links. A rank tie between two releases
/// carrying the same app id goes to the newer release id, and two versions of
/// one app in the same release (an amend can add a newer one) go to the higher
/// version, then the newer module, so the pick is the same on every run (#1224). See
/// <c>.design/object-explorer.md</c> ("Dependency links").</para>
///
/// <para><b>Tenant fence.</b> The dependency seed joins both ends of each link
/// to <c>oe_releases</c> and keeps only a dependency in the seed release's own
/// organisation, so a link row naming another tenant's release is never
/// followed even if one were written. The parent step carries the same
/// organisation predicate, so a parent pointer naming another tenant's release
/// ends the walk there; the import refuses such a parent in the first place
/// (see <see cref="ChainObjectResolution"/>).</para>
/// </summary>
internal static class ReleaseAncestrySql
{
    public const string Chain = """
        WITH RECURSIVE chain_walk AS (
            SELECT id, parent_release_id, 0 AS depth
            FROM oe_releases
            WHERE id = {0}
            UNION ALL
            SELECT dep.id, dep.parent_release_id, 1 AS depth
            FROM oe_release_dependencies d
            JOIN oe_releases seed ON seed.id = d.release_id
            JOIN oe_releases dep  ON dep.id  = d.dependency_release_id
                                 AND dep.organization_id = seed.organization_id
            WHERE d.release_id = {0}
            UNION ALL
            SELECT r.id, r.parent_release_id, c.depth + 2
            FROM oe_releases r
            JOIN chain_walk c ON r.id = c.parent_release_id
            WHERE r.organization_id = (SELECT organization_id FROM oe_releases WHERE id = {0})
        ),
        chain AS (
            SELECT id, MIN(depth) AS depth
            FROM chain_walk
            GROUP BY id
        )
        """;

    public const string WinningModules = Chain + """
        ,
        winning AS (
            SELECT DISTINCT ON (m.app_id) m.id, m.app_id
            FROM oe_modules m
            JOIN chain c ON c.id = m.release_id
            ORDER BY m.app_id, c.depth ASC, c.id DESC,
                     CASE WHEN m.version ~ '^[0-9]+(\.[0-9]+)*$'
                          THEN string_to_array(m.version, '.')::numeric[] END DESC NULLS LAST,
                     m.id DESC
        )
        """;

    public const string WinningModulesWithName = Chain + """
        ,
        winning AS (
            SELECT DISTINCT ON (m.app_id) m.id, m.app_id, m.name
            FROM oe_modules m
            JOIN chain c ON c.id = m.release_id
            ORDER BY m.app_id, c.depth ASC, c.id DESC,
                     CASE WHEN m.version ~ '^[0-9]+(\.[0-9]+)*$'
                          THEN string_to_array(m.version, '.')::numeric[] END DESC NULLS LAST,
                     m.id DESC
        )
        """;
}
