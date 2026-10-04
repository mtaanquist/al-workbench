using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "project_id",
                table: "user_notifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "project_id",
                table: "notification_digest_items",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_project_id",
                table: "user_notifications",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_digest_items_project_id",
                table: "notification_digest_items",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_digest_items_oe_projects_project_id",
                table: "notification_digest_items",
                column: "project_id",
                principalTable: "oe_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_notifications_oe_projects_project_id",
                table: "user_notifications",
                column: "project_id",
                principalTable: "oe_projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notification_digest_items_oe_projects_project_id",
                table: "notification_digest_items");

            migrationBuilder.DropForeignKey(
                name: "FK_user_notifications_oe_projects_project_id",
                table: "user_notifications");

            migrationBuilder.DropIndex(
                name: "IX_user_notifications_project_id",
                table: "user_notifications");

            migrationBuilder.DropIndex(
                name: "IX_notification_digest_items_project_id",
                table: "notification_digest_items");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "user_notifications");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "notification_digest_items");
        }
    }
}
