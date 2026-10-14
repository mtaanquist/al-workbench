using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "notified_online",
                table: "gateways",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gateway_commands",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    gateway_id = table.Column<int>(type: "integer", nullable: false),
                    verb = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    started_by_agent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_progress_sequence = table.Column<long>(type: "bigint", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    log = table.Column<string>(type: "text", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_commands", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_commands_gateways_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "gateways",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_commands_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_commands_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "gateway_command_files",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    command_id = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_command_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_command_files_gateway_commands_command_id",
                        column: x => x.command_id,
                        principalTable: "gateway_commands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_command_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_command_files_command_id",
                table: "gateway_command_files",
                column: "command_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_command_files_organization_id",
                table: "gateway_command_files",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_commands_gateway_id_status_due_at",
                table: "gateway_commands",
                columns: new[] { "gateway_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_commands_organization_id_status",
                table: "gateway_commands",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_commands_requested_by_user_id",
                table: "gateway_commands",
                column: "requested_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_command_files");

            migrationBuilder.DropTable(
                name: "gateway_commands");

            migrationBuilder.DropColumn(
                name: "notified_online",
                table: "gateways");
        }
    }
}
