using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "log_minimum_level",
                table: "system_settings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "log_retention_days",
                table: "system_settings",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.CreateTable(
                name: "app_log_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    worker = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    exception = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_log_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_log_entries_timestamp",
                table: "app_log_entries",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_app_log_entries_worker_id",
                table: "app_log_entries",
                columns: new[] { "worker", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_log_entries");

            migrationBuilder.DropColumn(
                name: "log_minimum_level",
                table: "system_settings");

            migrationBuilder.DropColumn(
                name: "log_retention_days",
                table: "system_settings");
        }
    }
}
