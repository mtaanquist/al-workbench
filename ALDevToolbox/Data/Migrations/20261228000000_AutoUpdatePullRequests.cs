using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoUpdatePullRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_update_pull_requests",
                table: "oe_projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "auto_update_pull_requests_blocked",
                table: "oe_projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "auto_update_pull_requests_by_user_id",
                table: "oe_projects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "github_update_pull_requests",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    repository = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    html_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_automatic = table.Column<bool>(type: "boolean", nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    superseded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_update_pull_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_update_pull_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oe_projects_auto_update_pull_requests_by_user_id",
                table: "oe_projects",
                column: "auto_update_pull_requests_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_github_update_pull_requests_org_repo_number",
                table: "github_update_pull_requests",
                columns: new[] { "organization_id", "repository", "pull_request_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_oe_projects_users_auto_update_pull_requests_by_user_id",
                table: "oe_projects",
                column: "auto_update_pull_requests_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_projects_users_auto_update_pull_requests_by_user_id",
                table: "oe_projects");

            migrationBuilder.DropTable(
                name: "github_update_pull_requests");

            migrationBuilder.DropIndex(
                name: "IX_oe_projects_auto_update_pull_requests_by_user_id",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "auto_update_pull_requests",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "auto_update_pull_requests_blocked",
                table: "oe_projects");

            migrationBuilder.DropColumn(
                name: "auto_update_pull_requests_by_user_id",
                table: "oe_projects");
        }
    }
}
