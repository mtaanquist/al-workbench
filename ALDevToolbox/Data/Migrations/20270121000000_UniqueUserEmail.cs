using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_organization_id_email",
                table: "users");

            // Emails were unique only per organisation, so an invite could give an
            // address a second account elsewhere (issue #1207). Before the
            // deployment-wide index can exist, every address held more than once
            // keeps its oldest account; the newer ones are disabled, signed out and
            // renamed to an address that can never receive mail or match a sign-in.
            migrationBuilder.Sql("""
                UPDATE users AS u
                SET email = 'duplicate-' || u.id || '+' || u.email,
                    status = 'Disabled',
                    credentials_changed_at = now()
                FROM (
                    SELECT id, row_number() OVER (PARTITION BY email ORDER BY created_at, id) AS rn
                    FROM users
                ) AS ranked
                WHERE ranked.id = u.id AND ranked.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_organization_id",
                table: "users",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_organization_id",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_users_organization_id_email",
                table: "users",
                columns: new[] { "organization_id", "email" },
                unique: true);
        }
    }
}
