using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "user_notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "notification_digest_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_notifications_subject_unread",
                table: "user_notifications",
                column: "subject",
                filter: "subject IS NOT NULL AND read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_digest_items_subject",
                table: "notification_digest_items",
                column: "subject",
                filter: "subject IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_notifications_subject_unread",
                table: "user_notifications");

            migrationBuilder.DropIndex(
                name: "ix_notification_digest_items_subject",
                table: "notification_digest_items");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "user_notifications");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "notification_digest_items");
        }
    }
}
