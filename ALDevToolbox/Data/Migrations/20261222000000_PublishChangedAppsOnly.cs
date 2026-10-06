using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublishChangedAppsOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "carried_from_build_id",
                table: "oe_project_build_artifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "changed_apps_only",
                table: "oe_pipelines",
                type: "boolean",
                nullable: false,
                // Existing pipelines take it up too; new ones get it from the entity.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "carried_from_build_id",
                table: "oe_project_build_artifacts");

            migrationBuilder.DropColumn(
                name: "changed_apps_only",
                table: "oe_pipelines");
        }
    }
}
