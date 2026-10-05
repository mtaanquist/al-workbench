using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildVersioningAndBranchRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_branch",
                table: "oe_release_pipelines",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "restrict_branch",
                table: "oe_release_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "auto_version",
                table: "oe_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Every existing deployment pipeline into a Production environment gets the
            // branch rule, allowing the branch its build pipeline builds today (null is
            // the repositories' default branch). Today's builds keep deploying; a later
            // change of the build pipeline's branch is what the rule then refuses.
            migrationBuilder.Sql("""
                UPDATE oe_release_pipelines r
                SET restrict_branch = true, allowed_branch = p.branch
                FROM oe_project_environments e, oe_pipelines p
                WHERE e.id = r.project_environment_id
                  AND lower(trim(e.type)) = 'production'
                  AND r.artifact_source = 'build'
                  AND p.id = r.build_pipeline_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_branch",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "restrict_branch",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "auto_version",
                table: "oe_pipelines");
        }
    }
}
