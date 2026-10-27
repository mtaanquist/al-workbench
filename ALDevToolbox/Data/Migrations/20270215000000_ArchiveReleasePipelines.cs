using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveReleasePipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "oe_release_pipelines",
                type: "timestamp with time zone",
                nullable: true);

            // An archived pipeline frees its name (#1466): narrow the per-project,
            // case-insensitive name uniqueness to pipelines that are neither deleted nor
            // archived. A functional index EF can't model, as in AddReleasePipelines.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_release_pipelines_project_name_active;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_release_pipelines_project_name_active " +
                "ON oe_release_pipelines (project_id, lower(name)) " +
                "WHERE deleted_at IS NULL AND archived_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails if an archived pipeline shares a name with an active one; rename one first.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_release_pipelines_project_name_active;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_release_pipelines_project_name_active " +
                "ON oe_release_pipelines (project_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "oe_release_pipelines");
        }
    }
}
