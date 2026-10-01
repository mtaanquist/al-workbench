using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineBuildTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bc_artifact_version",
                table: "oe_project_builds",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bc_target",
                table: "oe_project_builds",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "current");

            migrationBuilder.AddColumn<string>(
                name: "bc_target",
                table: "oe_pipelines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "current");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bc_artifact_version",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "bc_target",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "bc_target",
                table: "oe_pipelines");
        }
    }
}
