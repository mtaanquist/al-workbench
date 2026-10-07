using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations.ObjectExplorer;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<OeProject>
{
    public void Configure(EntityTypeBuilder<OeProject> entity)
    {
        entity.ToTable("oe_projects");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        entity.Property(e => e.ShortName).HasColumnName("short_name").HasMaxLength(50);
        entity.Property(e => e.Slug).HasColumnName("slug").HasMaxLength(60);
        entity.Property(e => e.DefaultArtifactCountry).HasColumnName("default_artifact_country").HasMaxLength(20);
        entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        // Stored as text so the column reads plainly and a new level never
        // renumbers existing rows. See .design/teams-and-visibility.md.
        entity.Property(e => e.Visibility)
            .HasColumnName("visibility")
            .HasConversion<string>()
            .HasMaxLength(20)
            // HasSentinel (#767): Public is the zero value, so an insert that
            // chose it explicitly was indistinguishable from one that said
            // nothing. Both mean Public today; only one of them still would if
            // the store default ever changed.
            .HasDefaultValue(ProjectVisibility.Public)
            .HasSentinel((ProjectVisibility)(-1))
            .IsRequired();
        entity.Property(e => e.DiscoveredExtensionsJson).HasColumnName("discovered_extensions_json");
        entity.Property(e => e.DiscoveredAt).HasColumnName("discovered_at");
        entity.Property(e => e.DiscoveryError).HasColumnName("discovery_error");
        entity.Property(e => e.AutoUpdatePullRequests).HasColumnName("auto_update_pull_requests").HasDefaultValue(false);
        entity.Property(e => e.AutoUpdatePullRequestsByUserId).HasColumnName("auto_update_pull_requests_by_user_id");
        entity.Property(e => e.AutoUpdatePullRequestsBlocked).HasColumnName("auto_update_pull_requests_blocked").HasMaxLength(500);

        // Customer information. See .design/solution-customer-info.md. The three enums
        // are text for the reason visibility is; all of it is optional.
        entity.Property(e => e.HostingType).HasColumnName("hosting_type").HasConversion<string>().HasMaxLength(30);
        entity.Property(e => e.BcVersion).HasColumnName("bc_version").HasMaxLength(50);
        entity.Property(e => e.LicenseType).HasColumnName("license_type").HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.UserExperience).HasColumnName("user_experience").HasConversion<string>().HasMaxLength(20);
        entity.Property(e => e.ClientUrl).HasColumnName("client_url").HasMaxLength(500);
        entity.Property(e => e.VoiceAccountNumber).HasColumnName("voice_account_number").HasMaxLength(30);
        entity.Property(e => e.AccessDescription).HasColumnName("access_description").HasMaxLength(4000);
        entity.Property(e => e.HostingNotes).HasColumnName("hosting_notes").HasMaxLength(4000);
        entity.Property(e => e.KnowledgeNotes).HasColumnName("knowledge_notes").HasMaxLength(4000);
        entity.Property(e => e.BcStorageQuotaKb).HasColumnName("bc_storage_quota_kb");
        entity.Property(e => e.BcStorageFetchedAt).HasColumnName("bc_storage_fetched_at");
        entity.Ignore(e => e.IsOnPremises);

        // Business Central SaaS connection (delivery). See .design/saas-delivery.md.
        entity.Property(e => e.BcTenantId).HasColumnName("bc_tenant_id");
        entity.Property(e => e.BcClientId).HasColumnName("bc_client_id");
        entity.Property(e => e.BcClientSecretEncrypted).HasColumnName("bc_client_secret_encrypted");
        entity.Property(e => e.BcClientSecretExpiresAt).HasColumnName("bc_client_secret_expires_at");
        entity.Property(e => e.BcCredentialsUpdatedAt).HasColumnName("bc_credentials_updated_at");
        entity.Property(e => e.BcTimeZone).HasColumnName("bc_time_zone").HasMaxLength(100);
        entity.Property(e => e.BcConnectionVerifiedAt).HasColumnName("bc_connection_verified_at");
        entity.Property(e => e.BcEnvironmentsFetchedAt).HasColumnName("bc_environments_fetched_at");

        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The owner. SET NULL on delete so removing a user doesn't cascade-delete
        // their projects — they become admin-managed until reassigned.
        entity.HasOne(e => e.CreatedByUser)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Who automatic update pull requests are opened as. SET NULL on delete, which
        // pauses them rather than removing the solution.
        entity.HasOne(e => e.AutoUpdatePullRequestsByUser)
            .WithMany()
            .HasForeignKey(e => e.AutoUpdatePullRequestsByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasMany(e => e.Repositories)
            .WithOne(r => r.Project!)
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Symbols)
            .WithOne(s => s.Project!)
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(e => e.Builds)
            .WithOne(b => b.Project!)
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // BC environments are configured on the ProjectEnvironment side
        // (FK project_id, cascade); the navigation is wired there.

        // Per-org name uniqueness on active rows so the picker doesn't show
        // duplicates. Case-INsensitive (lower(name)) to match the service's
        // case-insensitive pre-check — otherwise "CRONUS" and "cronus" pass the index
        // but the app rejects them, an inconsistency. EF can't model a functional
        // index, so it's created via raw SQL in the migration (and intentionally
        // not declared here). See issue #432.

        // Declared so EF keeps it: the slug index below starts with organization_id, but
        // it is partial, so it does not cover the foreign key for deleted rows.
        entity.HasIndex(e => e.OrganizationId).HasDatabaseName("IX_oe_projects_organization_id");

        // Slugs are stored lowercase, so a plain unique index is enough - unlike the
        // name above. Active rows only, so a deleted solution frees its address.
        entity.HasIndex(e => new { e.OrganizationId, e.Slug })
            .HasDatabaseName("ix_oe_projects_organization_id_slug")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
    }
}
