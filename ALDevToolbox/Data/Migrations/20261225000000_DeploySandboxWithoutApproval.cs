using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploySandboxWithoutApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "deploy_without_approval",
                table: "oe_release_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "deploy_without_approval_by_user_id",
                table: "oe_release_pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deployed_without_approval",
                table: "oe_project_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_oe_release_pipelines_deploy_without_approval_by_user_id",
                table: "oe_release_pipelines",
                column: "deploy_without_approval_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_release_pipelines_users_deploy_without_approval_by_user_~",
                table: "oe_release_pipelines",
                column: "deploy_without_approval_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_release_pipelines_users_deploy_without_approval_by_user_~",
                table: "oe_release_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_oe_release_pipelines_deploy_without_approval_by_user_id",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "deploy_without_approval",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "deploy_without_approval_by_user_id",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "deployed_without_approval",
                table: "oe_project_deliveries");
        }
    }
}
