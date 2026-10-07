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

            // Staged builds from before (#1118): read the repository off the release link,
            // https://github.com/{owner}/{repo}/releases/tag/{tag}, against the solution's
            // repositories. A link that matches none stays null and is set the next time
            // that release is staged.
            migrationBuilder.Sql("""
                UPDATE oe_project_builds b
                SET staged_from_repository_id = r.id
                FROM oe_project_repositories r
                WHERE b.pipeline_id IS NULL
                  AND b.github_release_tag IS NOT NULL
                  AND b.github_release_url IS NOT NULL
                  AND r.project_id = b.project_id
                  AND starts_with(lower(b.github_release_url),
                      lower(regexp_replace(rtrim(r.url, '/'), '\.git$', '')) || '/releases/');
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
