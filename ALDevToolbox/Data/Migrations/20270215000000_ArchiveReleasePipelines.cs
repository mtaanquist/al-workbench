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

            migrationBuilder.AddCheckConstraint(
                name: "ck_oe_release_pipelines_archived_is_disabled",
                table: "oe_release_pipelines",
                sql: "archived_at IS NULL OR disabled_at IS NOT NULL");

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
            // Without archived_at, an archived pipeline would come back as an active one and
            // could clash with a pipeline that took its name. Keep it hidden as deleted, so
            // the wider index can always be rebuilt.
            migrationBuilder.Sql(
                "UPDATE oe_release_pipelines SET deleted_at = archived_at " +
                "WHERE archived_at IS NOT NULL AND deleted_at IS NULL;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_oe_release_pipelines_project_name_active;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_oe_release_pipelines_project_name_active " +
                "ON oe_release_pipelines (project_id, lower(name)) " +
                "WHERE deleted_at IS NULL;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_oe_release_pipelines_archived_is_disabled",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "oe_release_pipelines");
        }
    }
}
