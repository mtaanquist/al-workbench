using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageConfirmationDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "package_confirmation_due",
                table: "oe_environment_upgrade_actions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_confirmation_due",
                table: "oe_environment_upgrade_actions");
        }
    }
}
