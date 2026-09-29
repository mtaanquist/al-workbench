using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> entity)
    {
        entity.ToTable("organizations");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.Name).HasColumnName("name").IsRequired();
        entity.Property(e => e.Slug).HasColumnName("slug").IsRequired();
        entity.HasIndex(e => e.Slug).IsUnique();
        entity.Property(e => e.IsPending).HasColumnName("is_pending").IsRequired();
        entity.Property(e => e.IsSystem).HasColumnName("is_system").IsRequired();
        entity.Property(e => e.StorageQuotaMb).HasColumnName("storage_quota_mb");
        entity.Property(e => e.McpEnabled).HasColumnName("mcp_enabled").IsRequired().HasDefaultValue(true);
        // text[] of ToolKey names; empty-array default so existing orgs backfill
        // with every tool on. See Domain/Tools/ToolCatalog.cs.
        entity.Property(e => e.DisabledTools)
            .HasColumnName("disabled_tools")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();
        // Same shape as disabled_tools: the tools that ask for a recent
        // second factor before use. See StepUpAuth.
        entity.Property(e => e.StepUpTools)
            .HasColumnName("step_up_tools")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        // Partial unique index on is_system=true: at most one system org per
        // deployment. Regular orgs aren't subject to the constraint because
        // Postgres ignores them in the partial index.
        entity.HasIndex(e => e.IsSystem)
            .IsUnique()
            .HasFilter("is_system = true")
            .HasDatabaseName("ix_organizations_is_system_singleton");
    }
}
