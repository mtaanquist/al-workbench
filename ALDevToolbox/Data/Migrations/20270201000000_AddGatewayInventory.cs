using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_instances",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    gateway_id = table.Column<int>(type: "integer", nullable: false),
                    machine = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    service_state = table.Column<string>(type: "text", nullable: true),
                    service_start_mode = table.Column<string>(type: "text", nullable: true),
                    service_account = table.Column<string>(type: "text", nullable: true),
                    database_server = table.Column<string>(type: "text", nullable: true),
                    database_instance = table.Column<string>(type: "text", nullable: true),
                    database_name = table.Column<string>(type: "text", nullable: true),
                    multitenant = table.Column<bool>(type: "boolean", nullable: true),
                    client_services_port = table.Column<int>(type: "integer", nullable: true),
                    soap_services_port = table.Column<int>(type: "integer", nullable: true),
                    odata_services_port = table.Column<int>(type: "integer", nullable: true),
                    management_services_port = table.Column<int>(type: "integer", nullable: true),
                    developer_services_port = table.Column<int>(type: "integer", nullable: true),
                    public_web_base_url = table.Column<string>(type: "text", nullable: true),
                    session_count = table.Column<int>(type: "integer", nullable: true),
                    session_users = table.Column<string>(type: "jsonb", nullable: true),
                    powershell = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    read_error = table.Column<string>(type: "text", nullable: true),
                    unavailable = table.Column<string>(type: "jsonb", nullable: false),
                    tenants_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    first_reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    missing_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_instances", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_instances_gateways_gateway_id",
                        column: x => x.gateway_id,
                        principalTable: "gateways",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_instances_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gateway_instance_licences",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    instance_id = table.Column<int>(type: "integer", nullable: false),
                    licensee = table.Column<string>(type: "text", nullable: true),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    text_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    parse_problems = table.Column<string>(type: "text", nullable: true),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_instance_licences", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_instance_licences_gateway_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "gateway_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_instance_licences_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gateway_instance_tenants",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    instance_id = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    state = table.Column<string>(type: "text", nullable: true),
                    database_name = table.Column<string>(type: "text", nullable: true),
                    session_count = table.Column<int>(type: "integer", nullable: true),
                    apps_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    apps_read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    missing_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_instance_tenants", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_instance_tenants_gateway_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "gateway_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_instance_tenants_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gateway_instance_licence_ranges",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    licence_id = table.Column<int>(type: "integer", nullable: false),
                    object_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_id = table.Column<int>(type: "integer", nullable: false),
                    to_id = table.Column<int>(type: "integer", nullable: false),
                    permissions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_instance_licence_ranges", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_instance_licence_ranges_gateway_instance_licences_l~",
                        column: x => x.licence_id,
                        principalTable: "gateway_instance_licences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_instance_licence_ranges_organizations_organization_~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gateway_instance_apps",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<int>(type: "integer", nullable: false),
                    tenant_row_id = table.Column<int>(type: "integer", nullable: false),
                    app_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sync_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    is_installed = table.Column<bool>(type: "boolean", nullable: true),
                    is_published = table.Column<bool>(type: "boolean", nullable: true),
                    missing_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_instance_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_instance_apps_gateway_instance_tenants_tenant_row_id",
                        column: x => x.tenant_row_id,
                        principalTable: "gateway_instance_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_gateway_instance_apps_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_apps_organization_id",
                table: "gateway_instance_apps",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_apps_tenant_row_id_app_id_version",
                table: "gateway_instance_apps",
                columns: new[] { "tenant_row_id", "app_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_licence_ranges_licence_id_object_type",
                table: "gateway_instance_licence_ranges",
                columns: new[] { "licence_id", "object_type" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_licence_ranges_organization_id",
                table: "gateway_instance_licence_ranges",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_licences_instance_id",
                table: "gateway_instance_licences",
                column: "instance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_licences_organization_id",
                table: "gateway_instance_licences",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_tenants_instance_id_tenant_id",
                table: "gateway_instance_tenants",
                columns: new[] { "instance_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instance_tenants_organization_id",
                table: "gateway_instance_tenants",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instances_gateway_id_machine_name",
                table: "gateway_instances",
                columns: new[] { "gateway_id", "machine", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_instances_organization_id",
                table: "gateway_instances",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_instance_apps");

            migrationBuilder.DropTable(
                name: "gateway_instance_licence_ranges");

            migrationBuilder.DropTable(
                name: "gateway_instance_tenants");

            migrationBuilder.DropTable(
                name: "gateway_instance_licences");

            migrationBuilder.DropTable(
                name: "gateway_instances");
        }
    }
}
