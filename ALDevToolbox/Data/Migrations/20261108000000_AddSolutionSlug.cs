using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionSlug : Migration
    {
        /// <summary>
        /// Gives every existing solution a slug. Public so a test can replay it against
        /// seeded rows. It derives every slug from the name again, so it is for this
        /// migration only, not a repair to re-run later.
        /// </summary>
        public const string BackfillSql = """
                WITH folded AS (
                    SELECT id, organization_id, deleted_at,
                           left(trim(both '-' FROM regexp_replace(
                               translate(
                                   lower(replace(replace(replace(replace(replace(replace(replace(replace(coalesce(nullif(trim(short_name), ''), name),
                                       'æ', 'ae'), 'Æ', 'ae'), 'å', 'aa'), 'Å', 'aa'), 'ß', 'ss'),
                                       'œ', 'oe'), 'Œ', 'oe'), 'þ', 'th')),
                                   'øØðÐđĐłŁáàâäãÁÀÂÄÃéèêëÉÈÊËíìîïÍÌÎÏóòôöõÓÒÔÖÕúùûüÚÙÛÜýÿÝñÑçÇ',
                                   'ooddddllaaaaaaaaaaeeeeeeeeiiiiiiiioooooooooouuuuuuuuyyynncc'),
                               '[^a-z0-9]+', '-', 'g')), 40) AS s
                    FROM oe_projects
                ),
                shaped AS (
                    SELECT id, organization_id, deleted_at,
                           CASE
                               WHEN trim(both '-' FROM s) = '' THEN 'solution'
                               WHEN trim(both '-' FROM s) ~ '^[0-9]+$' OR trim(both '-' FROM s) = 'new'
                                   THEN 'solution-' || trim(both '-' FROM s)
                               ELSE trim(both '-' FROM s)
                           END AS slug
                    FROM folded
                ),
                ranked AS (
                    SELECT id, slug,
                           CASE WHEN deleted_at IS NULL
                                THEN row_number() OVER (PARTITION BY organization_id, slug, deleted_at IS NULL ORDER BY id)
                                ELSE 1
                           END AS n
                    FROM shaped
                )
                UPDATE oe_projects p
                SET slug = CASE WHEN r.n = 1 THEN r.slug ELSE r.slug || '-' || p.id END
                FROM ranked r
                WHERE r.id = p.id;

                -- A suffixed slug can still equal another row's own ("CRONUS 5" beside a
                -- second "CRONUS" with id 5). The rare row left clashing keeps numeric
                -- links until someone gives it a slug, rather than failing the index.
                UPDATE oe_projects p
                SET slug = NULL
                WHERE p.deleted_at IS NULL
                  AND EXISTS (
                      SELECT 1 FROM oe_projects q
                      WHERE q.organization_id = p.organization_id
                        AND q.deleted_at IS NULL
                        AND q.slug = p.slug
                        AND q.id < p.id);
                """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "slug",
                table: "oe_projects",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            // Nullable so rows written outside ProjectService (fixtures, a future import)
            // still insert; a solution without a slug just keeps numeric links.
            //
            // Backfill: the short name when there is one, else the name, folded by an
            // approximation of SolutionSlug.Derive in SQL, good enough for
            // a starting value people can edit. Letters that do not fold to ASCII by
            // translate() become dashes; empty, all-digit and reserved results get the
            // "solution-" prefix Derive gives them. Active rows that still collide in
            // one org take their id as a suffix; the cut to 40 leaves room for both the
            // prefix and the suffix within the column's 60.
            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateIndex(
                name: "ix_oe_projects_organization_id_slug",
                table: "oe_projects",
                columns: new[] { "organization_id", "slug" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_projects_organization_id_slug",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "slug",
                table: "oe_projects");
        }
    }
}
