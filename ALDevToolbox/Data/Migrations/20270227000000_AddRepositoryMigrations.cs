using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repository_migrations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    source_project = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_project_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    sources = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    template_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    repository_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repository_migrations", x => x.id);
                    table.ForeignKey(
                        name: "FK_repository_migrations_oe_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "oe_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_repository_migrations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_repository_migrations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repository_migrations_created_by_user_id",
                table: "repository_migrations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_repository_migrations_organization_id_created_at",
                table: "repository_migrations",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_repository_migrations_project_id",
                table: "repository_migrations",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repository_migrations");
        }
    }
}
