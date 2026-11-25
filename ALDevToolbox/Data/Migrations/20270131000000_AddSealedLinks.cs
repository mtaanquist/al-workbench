using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSealedLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sealed_links",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    iv = table.Column<byte[]>(type: "bytea", nullable: false),
                    link_check_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    passphrase_salt = table.Column<byte[]>(type: "bytea", nullable: true),
                    passphrase_check_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    failed_passphrase_attempts = table.Column<int>(type: "integer", nullable: false),
                    max_views = table.Column<int>(type: "integer", nullable: false),
                    view_count = table.Column<int>(type: "integer", nullable: false),
                    notified_view_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    lockout_notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sealed_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_sealed_links_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sealed_links_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sealed_link_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    sealed_link_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sealed_link_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_sealed_link_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sealed_link_events_sealed_links_sealed_link_id",
                        column: x => x.sealed_link_id,
                        principalTable: "sealed_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sealed_link_events_organization_id",
                table: "sealed_link_events",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_sealed_link_events_sealed_link_id_occurred_at",
                table: "sealed_link_events",
                columns: new[] { "sealed_link_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sealed_links_created_by_user_id",
                table: "sealed_links",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sealed_links_organization_id_created_by_user_id_created_at",
                table: "sealed_links",
                columns: new[] { "organization_id", "created_by_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sealed_links_organization_id_expires_at",
                table: "sealed_links",
                columns: new[] { "organization_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sealed_links_token",
                table: "sealed_links",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sealed_link_events");

            migrationBuilder.DropTable(
                name: "sealed_links");
        }
    }
}
