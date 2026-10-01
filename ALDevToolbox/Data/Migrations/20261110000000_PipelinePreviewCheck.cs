using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelinePreviewCheck : Migration
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

            migrationBuilder.AddColumn<bool>(
                name: "preview_check",
                table: "oe_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "preview_check_blocked",
                table: "oe_pipelines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "preview_check_by_user_id",
                table: "oe_pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_pipelines_preview_check_by_user_id",
                table: "oe_pipelines",
                column: "preview_check_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_pipelines_users_preview_check_by_user_id",
                table: "oe_pipelines",
                column: "preview_check_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_pipelines_users_preview_check_by_user_id",
                table: "oe_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_oe_pipelines_preview_check_by_user_id",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "bc_artifact_version",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "bc_target",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "preview_check",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "preview_check_blocked",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "preview_check_by_user_id",
                table: "oe_pipelines");
        }
    }
}
