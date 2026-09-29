using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreBookedUploadPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "package_content",
                table: "oe_environment_upgrade_actions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "package_file_name",
                table: "oe_environment_upgrade_actions",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "package_content",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_file_name",
                table: "oe_environment_upgrade_actions");
        }
    }
}
