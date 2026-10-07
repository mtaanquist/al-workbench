using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GitHubUpdatePullRequestConfiguration : IEntityTypeConfiguration<GitHubUpdatePullRequest>
{
    public void Configure(EntityTypeBuilder<GitHubUpdatePullRequest> entity)
    {
        entity.ToTable("github_update_pull_requests");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(300).IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").HasMaxLength(20).IsRequired();
        entity.Property(e => e.PullRequestNumber).HasColumnName("pull_request_number").IsRequired();
        entity.Property(e => e.HtmlUrl).HasColumnName("html_url").HasMaxLength(500).IsRequired();
        entity.Property(e => e.IsAutomatic).HasColumnName("is_automatic").IsRequired();
        entity.Property(e => e.OpenedAt).HasColumnName("opened_at").IsRequired();
        entity.Property(e => e.SupersededAt).HasColumnName("superseded_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per pull request: the same number on the same repository is one
        // pull request however many times it was joined.
        entity.HasIndex(e => new { e.OrganizationId, e.Repository, e.PullRequestNumber })
            .IsUnique()
            .HasDatabaseName("ux_github_update_pull_requests_org_repo_number");
    }
}
