using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuildCommitAuthorEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_email",
                table: "oe_project_build_commits",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            // PersonRoles added roles with an empty-array default the model does not have;
            // a row written without roles would then belong to no one's notifications.
            migrationBuilder.Sql("ALTER TABLE oe_project_people ALTER COLUMN roles DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE oe_project_people ALTER COLUMN roles SET DEFAULT '{}';");


            migrationBuilder.DropColumn(
                name: "author_email",
                table: "oe_project_build_commits");
        }
    }
}
