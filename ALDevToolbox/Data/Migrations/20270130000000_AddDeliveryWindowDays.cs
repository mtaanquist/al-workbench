using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryWindowDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 127 is DeliveryDays.Every: every window set before days could be picked was daily.
            migrationBuilder.AddColumn<short>(
                name: "update_window_days",
                table: "oe_project_environments",
                type: "smallint",
                nullable: false,
                defaultValue: (short)127);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "update_window_days",
                table: "oe_project_environments");
        }
    }
}
