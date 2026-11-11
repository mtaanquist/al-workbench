using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradePermissionScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "upgrade_permission_scope",
                table: "organization_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "AssignedTeams");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "upgrade_permission_scope",
                table: "organization_settings");
        }
    }
}
