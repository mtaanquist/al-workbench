using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryMigrationRerunFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "add_template_extensions",
                table: "repository_migrations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "repository_github_id",
                table: "repository_migrations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "short_name",
                table: "repository_migrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_repository_ids",
                table: "repository_migrations",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "add_template_extensions",
                table: "repository_migrations");

            migrationBuilder.DropColumn(
                name: "repository_github_id",
                table: "repository_migrations");

            migrationBuilder.DropColumn(
                name: "short_name",
                table: "repository_migrations");

            migrationBuilder.DropColumn(
                name: "source_repository_ids",
                table: "repository_migrations");
        }
    }
}
