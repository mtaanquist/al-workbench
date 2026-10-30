using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

// The Customer tab's hand-kept lists. See .design/solution-customer-info.md. Enums are
// text for the reason oe_projects.visibility is.

internal sealed class ProjectContactConfiguration : IEntityTypeConfiguration<OeProjectContact>
{
    public void Configure(EntityTypeBuilder<OeProjectContact> entity)
    {
        entity.ToTable("oe_project_contacts");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(30).IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(e => e.Company).HasColumnName("company").HasMaxLength(100);
        entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(200);
        entity.Property(e => e.Phone).HasColumnName("phone").HasMaxLength(50);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasIndex(e => e.ProjectId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Project!).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectPersonConfiguration : IEntityTypeConfiguration<OeProjectPerson>
{
    public void Configure(EntityTypeBuilder<OeProjectPerson> entity)
    {
        entity.ToTable("oe_project_people");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.PrimitiveCollection(e => e.Roles).HasColumnName("roles").IsRequired()
            .ElementType().HasConversion<string>().HasMaxLength(30);
        entity.Property(e => e.Areas).HasColumnName("areas").HasMaxLength(250);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // One row per person per solution; their roles and areas are edited, not stacked.
        entity.HasIndex(e => new { e.ProjectId, e.UserId }).IsUnique();
        // Covers the cascade when a user is removed.
        entity.HasIndex(e => e.UserId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Project!).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.User!).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectIntegrationConfiguration : IEntityTypeConfiguration<OeProjectIntegration>
{
    public void Configure(EntityTypeBuilder<OeProjectIntegration> entity)
    {
        entity.ToTable("oe_project_integrations");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(e => e.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(20).IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasIndex(e => e.ProjectId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Project!).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
