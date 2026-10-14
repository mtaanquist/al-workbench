using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordRepositoryAccessConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "access_confirmed_at",
                table: "oe_project_repositories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "access_confirmed_by_user_id",
                table: "oe_project_repositories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "added_by_user_id",
                table: "oe_project_repositories",
                type: "integer",
                nullable: true);

            // Every repository already on a solution counts as confirmed, so builds keep
            // working on deploy; only repositories added or re-addressed from here on are
            // checked. Who added or confirmed them is unknown, so those stay null.
            // See .design/object-explorer-project-builds.md, "Confirmed repositories".
            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_repositories_access_confirmed_by_user",
                table: "oe_project_repositories",
                column: "access_confirmed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_oe_project_repositories_added_by_user",
                table: "oe_project_repositories",
                column: "added_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_repositories_users_access_confirmed_by_user_id",
                table: "oe_project_repositories",
                column: "access_confirmed_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_oe_project_repositories_users_added_by_user_id",
                table: "oe_project_repositories",
                column: "added_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <summary>
        /// Confirms every repository that is already on a solution when this runs. Public
        /// so <c>RecordRepositoryAccessConfirmationMigrationTests</c> can replay it.
        /// </summary>
        public const string BackfillSql =
            "UPDATE oe_project_repositories SET access_confirmed_at = now() WHERE access_confirmed_at IS NULL;";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_repositories_users_access_confirmed_by_user_id",
                table: "oe_project_repositories");

            migrationBuilder.DropForeignKey(
                name: "FK_oe_project_repositories_users_added_by_user_id",
                table: "oe_project_repositories");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_repositories_access_confirmed_by_user",
                table: "oe_project_repositories");

            migrationBuilder.DropIndex(
                name: "ix_oe_project_repositories_added_by_user",
                table: "oe_project_repositories");

            migrationBuilder.DropColumn(
                name: "access_confirmed_at",
                table: "oe_project_repositories");

            migrationBuilder.DropColumn(
                name: "access_confirmed_by_user_id",
                table: "oe_project_repositories");

            migrationBuilder.DropColumn(
                name: "added_by_user_id",
                table: "oe_project_repositories");
        }
    }
}
