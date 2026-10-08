using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordStagedReleaseRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "staged_from_repository_id",
                table: "oe_project_builds",
                type: "integer",
                nullable: true);

            // Staged builds from before (#1118): read owner/repo off the release link,
            // https://github.com/{owner}/{repo}/releases/tag/{tag}, and match it against the
            // solution's repository links however they were typed (www., a .git ending in
            // any case, a trailing path). A link that matches none stays null and is set the
            // next time that release is staged.
            migrationBuilder.Sql("""
                WITH repos AS (
                    SELECT r.id, r.project_id,
                           m[2] || '/' || regexp_replace(m[3], '\.git$', '') AS key
                    FROM oe_project_repositories r,
                         regexp_match(lower(btrim(r.url)), '^https?://(www\.)?github\.com/([^/]+)/([^/?#]+)') AS m
                ),
                releases AS (
                    SELECT b.id, b.project_id, m[2] || '/' || m[3] AS key
                    FROM oe_project_builds b,
                         regexp_match(lower(b.github_release_url), '^https://(www\.)?github\.com/([^/]+)/([^/]+)/releases/') AS m
                    WHERE b.pipeline_id IS NULL
                      AND b.github_release_tag IS NOT NULL
                )
                UPDATE oe_project_builds b
                SET staged_from_repository_id = (
                    SELECT min(repos.id) FROM repos
                    WHERE repos.project_id = releases.project_id AND repos.key = releases.key)
                FROM releases
                WHERE b.id = releases.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "staged_from_repository_id",
                table: "oe_project_builds");
        }
    }
}
