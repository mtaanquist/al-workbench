using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Services.Organizations;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ALDevToolbox.Tests.Schema;

/// <summary>
/// The other hand-maintained tenancy list (#665): the ~60 explicit
/// <c>ScopeToOrganization&lt;T&gt;</c> calls in <c>AppDbContext</c>. The EF query
/// filter is the only thing stopping one org's request from reading another
/// org's rows, and forgetting a call is invisible at compile time. These tests
/// make the omission a red build - for a table with its own
/// <c>organization_id</c>, and since #1426 for a table that reaches its
/// organisation through a required parent (the per-user secrets), and for a
/// filter that is there but never compares the organisation.
/// </summary>
public sealed class TenantQueryFilterTests
{
    /// <summary>
    /// Tenanted tables that deliberately carry no query filter, with the reason.
    /// Nothing else may be added here without a maintainer decision.
    /// </summary>
    private static readonly Dictionary<string, string> Unfiltered = new()
    {
        ["organization_usage_snapshots"] = "Written and read off-request by the usage scheduler and the SiteAdmin storage page via raw SQL, both cross-org by design.",
        ["per_tenant_backups"] = "SiteAdmin-only surface, read across orgs; every method gates on RequireSiteAdmin() first.",
        ["email_outbox"] = "Written by pre-auth flows with no organisation in scope and read by the SiteAdmin email page; its organization_id is a label for that page, not a fence. See issue #790.",
    };

    /// <summary>
    /// Tables with no <c>organization_id</c> but a required parent that is
    /// filtered, which deliberately carry no filter of their own, with the
    /// reason. Empty today: the shared tables with no organisation
    /// (<c>oe_file_contents</c>, the BCQuality and OpenIddict tables) hang off
    /// no filtered parent. Nothing may be added here without a maintainer
    /// decision.
    /// </summary>
    private static readonly Dictionary<string, string> UnfilteredChildren = new();

    [Fact]
    public void Every_entity_with_an_organization_id_is_scoped_by_a_query_filter()
    {
        UnscopedTenantTables(AppModelEntities()).Should().BeEmpty(
            "every tenanted entity needs a ScopeToOrganization<T>() call in AppDbContext.OnModelCreating");
    }

    [Fact]
    public void Every_entity_owned_by_a_filtered_parent_is_scoped_by_a_query_filter()
    {
        UnscopedChildTables(AppModelEntities()).Should().BeEmpty(
            "a table with no organization_id of its own that belongs to a filtered row (a user, a module) is " +
            "readable across organisations unless it is filtered through that parent, as AppDbContext does for " +
            "PasswordResetToken and the MFA tables");
    }

    [Fact]
    public void The_tables_scoped_through_their_user_are_covered_by_that_rule()
    {
        var covered = AppModelEntities()
            .Where(e => !HasOrganizationColumn(e) && RequiredFilteredParents(e).Any())
            .Select(e => e.GetTableName()!)
            .ToHashSet();

        covered.Should().Contain(TenantTableCatalog.TablesLinkedThroughUser.Keys);
    }

    /// <summary>
    /// "Has a filter" is not enough: <c>e =&gt; e.DeletedAt == null</c> on a
    /// tenanted table, or a hand-written filter that compares the wrong
    /// thing, would pass that check and leak. Every filter on a table scoped
    /// by organisation has to compare an organisation id with the acting one.
    /// </summary>
    [Fact]
    public void Every_tenant_filter_compares_with_the_acting_organisation()
    {
        FiltersThatIgnoreTheOrganisation(AppModelEntities()).Should().BeEmpty(
            "a tenant filter must compare an organisation id with the acting organisation " +
            "(ScopeToOrganization<T>(), or a navigation to a parent's OrganizationId)");
    }

    /// <summary>
    /// The three checks above pass on today's model, so this proves each one
    /// can fail: a small model with one of each mistake.
    /// </summary>
    [Fact]
    public void The_checks_catch_a_missing_or_unrelated_filter()
    {
        var options = new DbContextOptionsBuilder<MisconfiguredContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new MisconfiguredContext(options);
        var entities = ctx.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToList();

        UnscopedTenantTables(entities).Should().Equal("unscoped_parents");
        UnscopedChildTables(entities).Should().Equal("unscoped_children");
        FiltersThatIgnoreTheOrganisation(entities).Should().ContainSingle()
            .Which.Should().StartWith("soft_deleted_only:");
    }

    private static List<string> UnscopedTenantTables(IEnumerable<IEntityType> entities) =>
        entities
            .Where(HasOrganizationColumn)
            .Where(e => !HasFilter(e))
            .Select(e => e.GetTableName()!)
            .Distinct()
            .Where(t => !Unfiltered.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    private static List<string> UnscopedChildTables(IEnumerable<IEntityType> entities) =>
        entities
            .Where(e => !HasOrganizationColumn(e))
            .Where(e => RequiredFilteredParents(e).Any())
            .Where(e => !HasFilter(e))
            .Select(e => e.GetTableName()!)
            .Distinct()
            .Where(t => !UnfilteredChildren.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The filter has to name an organisation id (the row's own, by property
    /// or through a navigation) and the acting organisation's.
    /// </summary>
    private static List<string> FiltersThatIgnoreTheOrganisation(IEnumerable<IEntityType> entities) =>
        entities
            .Where(HasFilter)
            .Where(e => HasOrganizationColumn(e) || RequiredFilteredParents(e).Any())
            .Where(e => !ComparesOrganisation(FilterText(e)))
            .Select(e => $"{e.GetTableName()}: {FilterText(e)}")
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    private static readonly Regex RowOrganisation = new(@"\bOrganizationId\b(?!ForFilter)");

    private static bool ComparesOrganisation(string filter) =>
        filter.Contains("OrganizationIdForFilter", StringComparison.Ordinal) && RowOrganisation.IsMatch(filter);

    private static List<IEntityType> AppModelEntities()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only")
            .Options;
        using var ctx = new AppDbContext(options);
        return ctx.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToList();
    }

    private static bool HasOrganizationColumn(IEntityType e) =>
        e.GetProperties().Any(p => p.GetColumnName() == "organization_id");

    private static bool HasFilter(IEntityType e) => (e.GetDeclaredQueryFilters()?.Count ?? 0) > 0;

    private static string FilterText(IEntityType e) =>
        string.Join(" && ", e.GetDeclaredQueryFilters().Select(f => f.Expression?.ToString()));

    /// <summary>The filtered principals this entity cannot exist without.</summary>
    private static IEnumerable<IEntityType> RequiredFilteredParents(IEntityType e) =>
        e.GetForeignKeys()
            .Where(fk => fk.IsRequired && fk.PrincipalEntityType != e)
            .Select(fk => fk.PrincipalEntityType)
            .Where(HasFilter);

    private sealed class ScopedParent
    {
        public int Id { get; set; }
        public int OrganizationId { get; set; }
    }

    private sealed class UnscopedChild
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public ScopedParent? Parent { get; set; }
    }

    private sealed class UnscopedParent
    {
        public int Id { get; set; }
        public int OrganizationId { get; set; }
    }

    private sealed class SoftDeletedOnly
    {
        public int Id { get; set; }
        public int OrganizationId { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private sealed class MisconfiguredContext(DbContextOptions<MisconfiguredContext> options) : DbContext(options)
    {
        public int OrganizationIdForFilter => 0;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ScopedParent>(e =>
            {
                e.ToTable("scoped_parents");
                e.Property(p => p.OrganizationId).HasColumnName("organization_id");
                e.HasQueryFilter(p => p.OrganizationId == OrganizationIdForFilter);
            });
            modelBuilder.Entity<UnscopedChild>(e =>
            {
                e.ToTable("unscoped_children");
                e.HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId).IsRequired();
            });
            modelBuilder.Entity<UnscopedParent>(e =>
            {
                e.ToTable("unscoped_parents");
                e.Property(p => p.OrganizationId).HasColumnName("organization_id");
            });
            modelBuilder.Entity<SoftDeletedOnly>(e =>
            {
                e.ToTable("soft_deleted_only");
                e.Property(p => p.OrganizationId).HasColumnName("organization_id");
                e.HasQueryFilter(p => p.DeletedAt == null);
            });
        }
    }
}
