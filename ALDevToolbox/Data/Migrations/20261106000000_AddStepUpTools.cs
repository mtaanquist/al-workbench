using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepUpTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "step_up_tools",
                table: "organizations",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<int>(
                name: "step_up_window_minutes",
                table: "organizations",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<DateTime>(
                name: "strong_auth_at",
                table: "oauth_consents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "step_up_tools",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "step_up_window_minutes",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "strong_auth_at",
                table: "oauth_consents");
        }
    }
}
