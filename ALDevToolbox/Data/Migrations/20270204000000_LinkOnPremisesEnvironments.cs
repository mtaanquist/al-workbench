using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkOnPremisesEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_oe_project_environments_project_name",
                table: "oe_project_environments");

            migrationBuilder.AddColumn<int>(
                name: "gateway_instance_id",
                table: "oe_project_environments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "oe_project_environments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Online");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "oe_project_environments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_gateway_instance_id",
                table: "oe_project_environments",
                column: "gateway_instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_gateway_tenant",
                table: "oe_project_environments",
                columns: new[] { "gateway_instance_id", "tenant_id" },
                unique: true,
                filter: "kind = 'OnPremises' AND missing_since IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_project_id",
                table: "oe_project_environments",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_project_name",
                table: "oe_project_environments",
                columns: new[] { "project_id", "name" },
                unique: true,
                filter: "missing_since IS NULL OR kind = 'Online'");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_environments_gateway_instances_gateway_instance_~",
                table: "oe_project_environments",
                column: "gateway_instance_id",
                principalTable: "gateway_instances",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_environments_gateway_instances_gateway_instance_~",
                table: "oe_project_environments");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_environments_gateway_instance_id",
                table: "oe_project_environments");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_environments_gateway_tenant",
                table: "oe_project_environments");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_environments_project_id",
                table: "oe_project_environments");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_environments_project_name",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "gateway_instance_id",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "oe_project_environments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oe_project_environments");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_environments_project_name",
                table: "oe_project_environments",
                columns: new[] { "project_id", "name" },
                unique: true);
        }
    }
}
