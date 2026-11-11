using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// #691: the five Object Explorer fact tables carry no single-column
/// <c>organization_id</c> index. A production install reported zero scans on
/// all five while they cost about 2.8 GB and a btree write per ingested row,
/// so <c>OeFactTableForeignKeyIndexConvention</c> declines to create them. EF's
/// stock convention re-creates a removed FK index silently, which is why this
/// is pinned by a test rather than trusted to the configuration.
/// </summary>
public sealed class OeFactTableIndexTests
{
    private static readonly string[] FactTables =
    [
        "oe_module_objects", "oe_module_references", "oe_module_symbols",
        "oe_module_variables", "oe_module_system_references",
    ];

    [Fact]
    public void Fact_tables_have_no_bare_organization_id_index_but_keep_the_foreign_key()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        var entities = ctx.Model.GetEntityTypes()
            .Where(e => FactTables.Contains(e.GetTableName()))
            .ToList();
        entities.Select(e => e.GetTableName()).Should().BeEquivalentTo(FactTables);

        foreach (var entity in entities)
        {
            var bare = entity.GetIndexes()
                .Where(i => i.Properties.Count == 1 && ((IReadOnlyProperty)i.Properties[0]).GetColumnName() == "organization_id")
                .ToList();
            bare.Should().BeEmpty($"{entity.GetTableName()} must not index organization_id alone (#691)");

            entity.GetForeignKeys()
                .Should().Contain(fk => fk.Properties.Count == 1 && fk.Properties[0].GetColumnName() == "organization_id",
                    $"{entity.GetTableName()} keeps its organisation foreign key; only the index goes");
        }
    }

    /// <summary>
    /// The replacement convention must only touch the five fact tables: every
    /// other foreign key in the model still gets its covering index.
    ///
    /// <para>This is the guard that stops an index audit dropping something a
    /// referential action needs. #723 proposed dropping
    /// <c>ix_oe_module_system_references_source_symbol</c> for reading zero
    /// scans; it covers a <c>SET NULL</c> foreign key, and measured on the real
    /// schema a release delete goes from 17 seconds to not finishing inside
    /// ReleaseManagementService's ten-minute timeout without it. See the
    /// migration-discipline section of CLAUDE.md.</para>
    /// </summary>
    [Fact]
    public void Every_other_foreign_key_still_has_a_covering_index()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);

        var uncovered = ctx.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !FactTables.Contains(e.GetTableName()))
            .SelectMany(e => e.GetForeignKeys().Select(fk => (Entity: e, Fk: fk)))
            .Where(x => !x.Entity.GetIndexes().Any(i => Covers(i.Properties.Cast<IReadOnlyProperty>().ToList(), x.Fk.Properties))
                        && !x.Entity.GetKeys().Any(k => Covers(k.Properties, x.Fk.Properties)))
            .Select(x => $"{x.Entity.GetTableName()}({string.Join(",", x.Fk.Properties.Select(p => p.GetColumnName()))})")
            .OrderBy(s => s)
            .ToList();

        uncovered.Should().BeEmpty("the replaced convention must behave like the stock one everywhere else");
    }

    private static bool Covers(
        IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty> indexProperties,
        IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty> fkProperties)
        => indexProperties.Count >= fkProperties.Count
           && fkProperties.Select((p, i) => indexProperties[i] == p).All(match => match);
}
