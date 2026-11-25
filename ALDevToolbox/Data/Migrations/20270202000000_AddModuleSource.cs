using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "source_checked_at",
                table: "modules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_commit_sha",
                table: "modules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_dependencies_json",
                table: "modules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_error",
                table: "modules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "source_fetched_at",
                table: "modules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_path",
                table: "modules",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_ref",
                table: "modules",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_repository",
                table: "modules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_skipped_json",
                table: "modules",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_checked_at",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_commit_sha",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_dependencies_json",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_error",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_fetched_at",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_path",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_ref",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_repository",
                table: "modules");

            migrationBuilder.DropColumn(
                name: "source_skipped_json",
                table: "modules");
        }
    }
}
