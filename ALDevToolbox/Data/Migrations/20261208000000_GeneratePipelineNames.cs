using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class GeneratePipelineNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "name_is_custom",
                table: "oe_release_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "name_is_custom",
                table: "oe_pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <summary>
        /// Renames every active pipeline to the name it would be given today (see
        /// <c>PipelineNames</c>, whose rules this repeats in SQL): build pipelines first,
        /// since a deployment pipeline is named after its build pipeline. Renames run one
        /// row at a time against the live unique index, so a name already taken in the
        /// solution is skipped rather than failing the upgrade; a second pass catches a
        /// name that was only taken by a row renamed later in the first. A pipeline left
        /// with its old name is marked custom, the same as one a person named because the
        /// generated name was taken. Soft-deleted rows keep their names. Public so a test
        /// can run it against rows the empty test database never has.
        /// </summary>
        public const string BackfillSql = """
            CREATE FUNCTION pg_temp.aldt_build_pipeline_name(p_branch text, p_selection text, p_discovered text)
            RETURNS text LANGUAGE plpgsql AS $fn$
            DECLARE
                base text := coalesce(nullif(btrim(p_branch), ''), 'Default branch');
                ids jsonb;
                n int;
                ext text;
                result text;
            BEGIN
                BEGIN
                    ids := nullif(btrim(coalesce(p_selection, '')), '')::jsonb;
                EXCEPTION WHEN others THEN
                    ids := NULL;
                END;
                IF ids IS NULL OR jsonb_typeof(ids) <> 'array' OR jsonb_array_length(ids) = 0 THEN
                    result := base;
                ELSE
                    n := jsonb_array_length(ids);
                    IF n = 1 THEN
                        BEGIN
                            -- The first entry for the id, as the C# side takes it.
                            SELECT nullif(btrim(e->>'Name'), '') INTO ext
                            FROM jsonb_array_elements(p_discovered::jsonb) WITH ORDINALITY AS d(e, ord)
                            WHERE lower(btrim(btrim(e->>'AppId'), '{}')) = lower(btrim(btrim(ids->>0), '{}'))
                            ORDER BY ord
                            LIMIT 1;
                        EXCEPTION WHEN others THEN
                            ext := NULL;
                        END;
                        result := base || ' (' || coalesce(ext, '1 extension') || ')';
                    ELSE
                        result := base || ' (' || n || ' extensions)';
                    END IF;
                END IF;
                RETURN rtrim(left(result, 200));
            END
            $fn$;

            -- The source is shortened rather than the environment when the whole is too long.
            CREATE FUNCTION pg_temp.aldt_deployment_pipeline_name(p_id int)
            RETURNS text LANGUAGE sql STABLE AS $fn$
                SELECT rtrim(left(
                    CASE WHEN length(src) + length(dest) > 200 AND length(dest) < 200
                         THEN rtrim(left(src, 200 - length(dest))) ELSE src END || dest, 200))
                FROM (
                    SELECT CASE
                               WHEN r.artifact_source = 'github_release' THEN btrim(repo.display_name) || ' releases'
                               ELSE btrim(bp.name)
                           END AS src,
                           ' to ' || btrim(e.name) AS dest
                    FROM oe_release_pipelines r
                JOIN oe_project_environments e ON e.id = r.project_environment_id
                LEFT JOIN oe_pipelines bp ON bp.id = r.build_pipeline_id AND r.artifact_source = 'build'
                LEFT JOIN oe_project_repositories repo ON repo.id = r.github_release_repository_id AND r.artifact_source = 'github_release'
                    WHERE r.id = p_id
                ) parts
            $fn$;

            DO $do$
            DECLARE
                row record;
                pass int;
            BEGIN
                FOR pass IN 1..2 LOOP
                    FOR row IN
                        SELECT p.id, p.project_id, p.name,
                               pg_temp.aldt_build_pipeline_name(p.branch, p.requested_app_ids_json, pr.discovered_extensions_json) AS target
                        FROM oe_pipelines p
                        JOIN oe_projects pr ON pr.id = p.project_id
                        WHERE p.deleted_at IS NULL
                        ORDER BY p.project_id, p.id
                    LOOP
                        IF row.name <> row.target AND NOT EXISTS (
                            SELECT 1 FROM oe_pipelines o
                            WHERE o.project_id = row.project_id AND o.deleted_at IS NULL
                              AND o.id <> row.id AND lower(o.name) = lower(row.target))
                        THEN
                            UPDATE oe_pipelines SET name = row.target WHERE id = row.id;
                        END IF;
                    END LOOP;
                END LOOP;

                UPDATE oe_pipelines p
                SET name_is_custom = p.name <> pg_temp.aldt_build_pipeline_name(p.branch, p.requested_app_ids_json, pr.discovered_extensions_json)
                FROM oe_projects pr
                WHERE pr.id = p.project_id AND p.deleted_at IS NULL;

                FOR pass IN 1..2 LOOP
                    FOR row IN
                        SELECT r.id, r.project_id, r.name, pg_temp.aldt_deployment_pipeline_name(r.id) AS target
                        FROM oe_release_pipelines r
                        WHERE r.deleted_at IS NULL
                        ORDER BY r.project_id, r.id
                    LOOP
                        IF row.target IS NOT NULL AND row.name <> row.target AND NOT EXISTS (
                            SELECT 1 FROM oe_release_pipelines o
                            WHERE o.project_id = row.project_id AND o.deleted_at IS NULL
                              AND o.id <> row.id AND lower(o.name) = lower(row.target))
                        THEN
                            UPDATE oe_release_pipelines SET name = row.target WHERE id = row.id;
                        END IF;
                    END LOOP;
                END LOOP;

                -- A pipeline whose source is gone (its repository removed) has no name to
                -- compare against; it keeps its name, unmarked, until it is next saved.
                UPDATE oe_release_pipelines r
                SET name_is_custom = r.name <> pg_temp.aldt_deployment_pipeline_name(r.id)
                WHERE r.deleted_at IS NULL AND pg_temp.aldt_deployment_pipeline_name(r.id) IS NOT NULL;
            END
            $do$;

            DROP FUNCTION pg_temp.aldt_deployment_pipeline_name(int);
            DROP FUNCTION pg_temp.aldt_build_pipeline_name(text, text, text);
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name_is_custom",
                table: "oe_release_pipelines");

            migrationBuilder.DropColumn(
                name: "name_is_custom",
                table: "oe_pipelines");
        }
    }
}
