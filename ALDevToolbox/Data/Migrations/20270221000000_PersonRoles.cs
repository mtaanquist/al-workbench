using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each person keeps the one role they had as their only role (#1462).
            migrationBuilder.AddColumn<string[]>(
                name: "roles",
                table: "oe_project_people",
                type: "character varying(30)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.Sql("UPDATE oe_project_people SET roles = ARRAY[role]::character varying(30)[];");
            // The empty-array default only fills the existing rows; the model has none, so a
            // row written without roles fails instead of quietly belonging to no audience.
            migrationBuilder.Sql("ALTER TABLE oe_project_people ALTER COLUMN roles DROP DEFAULT;");

            migrationBuilder.DropColumn(
                name: "role",
                table: "oe_project_people");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "oe_project_people",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Consultant");

            // Back to one role: the first in the stored order, which follows the enum's order.
            migrationBuilder.Sql("UPDATE oe_project_people SET role = roles[1] WHERE cardinality(roles) > 0;");

            migrationBuilder.DropColumn(
                name: "roles",
                table: "oe_project_people");
        }
    }
}
