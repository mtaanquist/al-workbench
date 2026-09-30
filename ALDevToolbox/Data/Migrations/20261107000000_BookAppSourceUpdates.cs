using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookAppSourceUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_name",
                table: "oe_environment_upgrade_actions",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "update_prerequisite_app_ids",
                table: "oe_environment_upgrade_actions",
                type: "uuid[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "app_name",
                table: "oe_environment_upgrade_actions");

            migrationBuilder.DropColumn(
                name: "update_prerequisite_app_ids",
                table: "oe_environment_upgrade_actions");
        }
    }
}
