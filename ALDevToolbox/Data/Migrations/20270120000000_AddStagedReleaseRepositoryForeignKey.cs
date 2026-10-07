using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStagedReleaseRepositoryForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column had no foreign key until now (#1178), so a staged build can still
            // name a repository that was removed from its solution since. Clear those first
            // or the constraint can't be created; staging the tag again records the current
            // repository.
            migrationBuilder.Sql("""
                UPDATE oe_project_builds b
                SET staged_from_repository_id = NULL
                WHERE b.staged_from_repository_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM oe_project_repositories r
                      WHERE r.id = b.staged_from_repository_id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_builds_staged_from_repository",
                table: "oe_project_builds",
                column: "staged_from_repository_id",
                filter: "staged_from_repository_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_builds_oe_project_repositories_staged_from_repos~",
                table: "oe_project_builds",
                column: "staged_from_repository_id",
                principalTable: "oe_project_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_builds_oe_project_repositories_staged_from_repos~",
                table: "oe_project_builds");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_builds_staged_from_repository",
                table: "oe_project_builds");
        }
    }
}
