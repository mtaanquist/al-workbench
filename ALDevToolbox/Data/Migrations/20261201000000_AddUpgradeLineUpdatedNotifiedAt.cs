using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradeLineUpdatedNotifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "updated_notified_at",
                table: "oe_environment_upgrade_lines",
                type: "timestamp with time zone",
                nullable: true);

            // Lines already on target before this column existed are old news: stamp them,
            // so the first sweep after the upgrade does not send a backlog of notices.
            migrationBuilder.Sql(@"
                UPDATE oe_environment_upgrade_lines l
                SET updated_notified_at = now()
                FROM oe_environment_upgrades u, oe_project_environments e
                WHERE l.upgrade_id = u.id AND l.environment_id = e.id
                  AND l.is_open AND l.checked_at IS NULL
                  AND e.version ~ '^[0-9]+\.[0-9]+' AND u.target_version ~ '^[0-9]+\.[0-9]+'
                  AND (split_part(e.version, '.', 1)::int, split_part(substring(e.version from '^[0-9]+\.[0-9]+'), '.', 2)::int)
                   >= (split_part(u.target_version, '.', 1)::int, split_part(substring(u.target_version from '^[0-9]+\.[0-9]+'), '.', 2)::int);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_notified_at",
                table: "oe_environment_upgrade_lines");
        }
    }
}
