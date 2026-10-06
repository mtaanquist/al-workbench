using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DriftAgainstProductionEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "environment_id",
                table: "github_repository_drift",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_repository_drift_environment_id",
                table: "github_repository_drift",
                column: "environment_id");

            migrationBuilder.AddForeignKey(
                name: "FK_github_repository_drift_oe_project_environments_environment~",
                table: "github_repository_drift",
                column: "environment_id",
                principalTable: "oe_project_environments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_github_repository_drift_oe_project_environments_environment~",
                table: "github_repository_drift");

            migrationBuilder.DropIndex(
                name: "ix_github_repository_drift_environment_id",
                table: "github_repository_drift");

            migrationBuilder.DropColumn(
                name: "environment_id",
                table: "github_repository_drift");
        }
    }
}
