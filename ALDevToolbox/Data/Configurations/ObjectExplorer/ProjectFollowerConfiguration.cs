using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectFollowerConfiguration : IEntityTypeConfiguration<OeProjectFollower>
{
    public void Configure(EntityTypeBuilder<OeProjectFollower> entity)
    {
        entity.ToTable("oe_project_followers");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        entity.Property(e => e.UserId).HasColumnName("user_id").IsRequired();
        entity.Property(e => e.Following).HasColumnName("following").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // One choice per person per solution; it is changed, not stacked.
        entity.HasIndex(e => new { e.ProjectId, e.UserId }).IsUnique();
        // Covers the cascade when a user is removed.
        entity.HasIndex(e => e.UserId);

        entity.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Project!).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.User!).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
