using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildOnPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "head_repository_id",
                table: "oe_project_builds",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "build_on_push",
                table: "oe_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "build_on_push_blocked",
                table: "oe_pipelines",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "build_on_push_by_user_id",
                table: "oe_pipelines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oe_pipelines_build_on_push_by_user_id",
                table: "oe_pipelines",
                column: "build_on_push_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_pipelines_users_build_on_push_by_user_id",
                table: "oe_pipelines",
                column: "build_on_push_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_pipelines_users_build_on_push_by_user_id",
                table: "oe_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_oe_pipelines_build_on_push_by_user_id",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "head_repository_id",
                table: "oe_project_builds");

            migrationBuilder.DropColumn(
                name: "build_on_push",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "build_on_push_blocked",
                table: "oe_pipelines");

            migrationBuilder.DropColumn(
                name: "build_on_push_by_user_id",
                table: "oe_pipelines");
        }
    }
}
