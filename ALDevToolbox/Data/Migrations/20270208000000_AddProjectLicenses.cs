using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectLicenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "license_expiry_notice_days",
                table: "oe_project_environments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "license_expiry_notice_for",
                table: "oe_project_environments",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oe_project_licenses",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    project_environment_id = table.Column<int>(type: "integer", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size = table.Column<int>(type: "integer", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    licence_target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    gateway_command_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oe_project_licenses", x => x.id);
                    table.ForeignKey(
                        name: "FK_oe_project_licenses_oe_project_environments_project_environ~",
                        column: x => x.project_environment_id,
                        principalTable: "oe_project_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oe_project_licenses_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_licenses_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oe_project_licenses_users_uploaded_by_user_id",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_licenses_environment_uploaded",
                table: "oe_project_licenses",
                columns: new[] { "project_environment_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_oe_project_licenses_organization_id",
                table: "oe_project_licenses",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_licenses_project_id",
                table: "oe_project_licenses",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_licenses_uploaded_by_user_id",
                table: "oe_project_licenses",
                column: "uploaded_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oe_project_licenses");

            migrationBuilder.DropColumn(
                name: "license_expiry_notice_days",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "license_expiry_notice_for",
                table: "oe_project_environments");
        }
    }
}
