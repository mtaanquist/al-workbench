using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveSessionUsersToTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Who is signed in moves from the tier to each tenant (#1787). Nothing to carry
            // over: the gateway sends the names again with its next reading.
            migrationBuilder.DropColumn(
                name: "session_users",
                table: "gateway_instances");

            migrationBuilder.AddColumn<string>(
                name: "session_users",
                table: "gateway_instance_tenants",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_users",
                table: "gateway_instance_tenants");

            migrationBuilder.AddColumn<string>(
                name: "session_users",
                table: "gateway_instances",
                type: "jsonb",
                nullable: true);
        }
    }
}
