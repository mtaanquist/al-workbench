using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class FollowStartedUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "update_checked_at",
                table: "oe_environment_upgrade_actions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_result",
                table: "oe_environment_upgrade_actions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_env_upgrade_actions_follow",
                table: "oe_environment_upgrade_actions",
                column: "sent_at",
                filter: "kind = 'RunNow' AND status = 'Sent' AND update_result IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_env_upgrade_actions_follow",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "update_checked_at",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "update_result",
                table: "oe_environment_upgrade_actions");
        }
    }
}
