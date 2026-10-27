using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidenTranslationMemoryRankIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory");

            migrationBuilder.CreateIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory",
                columns: new[] { "organization_id", "score", "hit_count", "last_seen_at", "id" },
                descending: new[] { false, true, true, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory");

            migrationBuilder.CreateIndex(
                name: "ix_translation_memory_rank",
                table: "translation_memory",
                columns: new[] { "organization_id", "score", "hit_count" },
                descending: new[] { false, true, true });
        }
    }
}
