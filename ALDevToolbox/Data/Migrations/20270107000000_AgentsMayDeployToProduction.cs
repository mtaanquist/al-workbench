using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentsMayDeployToProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "agents_may_deploy_to_production",
                table: "organization_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "started_by_agent",
                table: "oe_project_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agents_may_deploy_to_production",
                table: "organization_settings");

            migrationBuilder.DropColumn(
                name: "started_by_agent",
                table: "oe_project_deliveries");
        }
    }
}
