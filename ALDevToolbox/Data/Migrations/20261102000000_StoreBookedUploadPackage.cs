using System;
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
            migrationBuilder.AddColumn<Guid>(
                name: "package_batch_id",
                table: "oe_environment_upgrade_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "package_batch_order",
                table: "oe_environment_upgrade_actions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "package_bc_app_id",
                table: "oe_environment_upgrade_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "package_bc_operation_id",
                table: "oe_environment_upgrade_actions",
                type: "uuid",
                nullable: true);

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
                name: "package_batch_id",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_batch_order",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_bc_app_id",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_bc_operation_id",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_content",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "package_file_name",
                table: "oe_environment_upgrade_actions");
        }
    }
}
