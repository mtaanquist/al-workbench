using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeployToOnPremises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "unpublish_previous",
                table: "oe_release_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "gateway_command_id",
                table: "oe_project_deliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "gateway_log_offset",
                table: "oe_project_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "tiers_restarted",
                table: "oe_project_deliveries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "project_build_artifact_id",
                table: "gateway_command_files",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_command_files_project_build_artifact_id",
                table: "gateway_command_files",
                column: "project_build_artifact_id");

            migrationBuilder.AddForeignKey(
                name: "FK_gateway_command_files_oe_project_build_artifacts_project_bu~",
                table: "gateway_command_files",
                column: "project_build_artifact_id",
                principalTable: "oe_project_build_artifacts",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_gateway_command_files_oe_project_build_artifacts_project_bu~",
                table: "gateway_command_files");

            migrationBuilder.DropIndex(
                name: "IX_gateway_command_files_project_build_artifact_id",
                table: "gateway_command_files");

            migrationBuilder.DropColumn(
                name: "unpublish_previous",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "gateway_command_id",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "gateway_log_offset",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "tiers_restarted",
                table: "oe_project_deliveries");

            migrationBuilder.DropColumn(
                name: "project_build_artifact_id",
                table: "gateway_command_files");
        }
    }
}
