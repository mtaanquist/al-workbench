using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ALDevToolbox.Data.Configurations;

internal sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettings>
{

    public void Configure(EntityTypeBuilder<OrganizationSettings> entity)
    {
        var jsonOptions = PersistenceJson.Options;
        var rulesetConverter = new ValueConverter<GitHubRepositoryRuleset?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, jsonOptions),
            v => v == null ? null : JsonSerializer.Deserialize<GitHubRepositoryRuleset>(v, jsonOptions));

        entity.ToTable("organization_settings");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.DefaultPublisher).HasColumnName("default_publisher").IsRequired();
        entity.Property(e => e.DefaultUrl).HasColumnName("default_url");
        entity.Property(e => e.DefaultLogo).HasColumnName("default_logo");
        // text[] gives us native Postgres array semantics; the value comparer
        // round-trips through a List<string> on the C# side without needing a
        // JSON value converter.
        entity.Property(e => e.DefaultSupportedCountries)
            .HasColumnName("default_supported_countries")
            .HasColumnType("text[]")
            .IsRequired();
        entity.Property(e => e.DefaultIdRangeFrom).HasColumnName("default_id_range_from").IsRequired();
        entity.Property(e => e.DefaultIdRangeTo).HasColumnName("default_id_range_to").IsRequired();
        entity.Property(e => e.DefaultBrief).HasColumnName("default_brief").IsRequired();
        entity.Property(e => e.DefaultCoreDescription).HasColumnName("default_core_description").IsRequired();
        entity.Property(e => e.CodeWorkspaceJson).HasColumnName("code_workspace_json").IsRequired();
        entity.Property(e => e.CookbookGuidance).HasColumnName("cookbook_guidance").IsRequired();
        // Naming conventions (#757). Stored as text like local_login_policy so a
        // psql session shows "PascalCase" rather than an integer nobody can
        // decode, and so adding a style later can't renumber the stored values.
        // HasSentinel on each: the store default only exists to backfill the
        // rows that predate these columns. Without a sentinel EF treats the
        // CLR default (PascalCase, Hidden - both 0) as "not set" and lets the
        // store default overwrite it, so an org that picks Hidden would keep
        // being saved as PerWorkspace. An out-of-range sentinel is never a real
        // choice, so every real choice is written explicitly.
        entity.Property(e => e.NamingFolderStyle)
            .HasColumnName("naming_folder_style").HasConversion<string>().HasMaxLength(32)
            .IsRequired().HasDefaultValue(NamingStyle.PascalCase).HasSentinel((NamingStyle)(-1));
        entity.Property(e => e.NamingRepositoryStyle)
            .HasColumnName("naming_repository_style").HasConversion<string>().HasMaxLength(32)
            .IsRequired().HasDefaultValue(NamingStyle.KebabCase).HasSentinel((NamingStyle)(-1));
        entity.Property(e => e.ExtensionPrefixMode)
            .HasColumnName("extension_prefix_mode").HasConversion<string>().HasMaxLength(32)
            .IsRequired().HasDefaultValue(ExtensionPrefixMode.PerWorkspace).HasSentinel((ExtensionPrefixMode)(-1));
        // Nullable: "the organisation has no prefix of its own" is a different
        // thing from an empty one, and the resolver falls back to the short name.
        entity.Property(e => e.ExtensionPrefix).HasColumnName("extension_prefix").HasMaxLength(50);
        entity.Property(e => e.RequireStrongAuth).HasColumnName("require_strong_auth").IsRequired();
        entity.Property(e => e.AutoJoinVerifiedDomainUsers).HasColumnName("auto_join_verified_domain_users").IsRequired();
        entity.Property(e => e.MachineTranslationProvider)
            .HasColumnName("machine_translation_provider").IsRequired().HasDefaultValue("deepl");
        entity.Property(e => e.MachineTranslationApiKeyEncrypted)
            .HasColumnName("machine_translation_api_key_encrypted");
        // HasSentinel for the same reason as the naming styles above (#767):
        // MtTrigger.Off is 0, so without it EF reads an explicit Off on insert as
        // "not set" and lets the store default decide.
        entity.Property(e => e.MachineTranslationTrigger)
            .HasColumnName("machine_translation_trigger").HasConversion<int>().IsRequired()
            .HasDefaultValue(ALDevToolbox.Domain.ValueObjects.MtTrigger.Off).HasSentinel((ALDevToolbox.Domain.ValueObjects.MtTrigger)(-1));
        entity.Property(e => e.AutoImportReleasesEnabled)
            .HasColumnName("auto_import_releases_enabled").IsRequired().HasDefaultValue(false);
        // 100 chars fits a generous comma-separated country list (codes are 2
        // chars each, so ~33 codes — far beyond any real localisation set).
        entity.Property(e => e.AutoImportCountry).HasColumnName("auto_import_country").HasMaxLength(100);
        entity.Property(e => e.AutoImportLastRunAt).HasColumnName("auto_import_last_run_at");
        entity.Property(e => e.AutoImportPreviewsEnabled)
            .HasColumnName("auto_import_previews_enabled").IsRequired().HasDefaultValue(false);
        // text[] like default_supported_countries; empty array default so the
        // NOT NULL column backfills on existing rows (empty = all providers allowed).
        entity.Property(e => e.AllowedRepositoryProviders)
            .HasColumnName("allowed_repository_providers")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();
        entity.Property(e => e.EntraEnabled)
            .HasColumnName("entra_enabled").IsRequired().HasDefaultValue(false);
        // text[] like default_supported_countries; empty array default so the
        // NOT NULL column backfills on existing rows.
        entity.Property(e => e.EntraAllowedTenantIds)
            .HasColumnName("entra_allowed_tenant_ids")
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();
        entity.Property(e => e.EntraClientId).HasColumnName("entra_client_id").HasMaxLength(64);
        entity.Property(e => e.EntraClientSecretEncrypted).HasColumnName("entra_client_secret_encrypted");
        entity.Property(e => e.BcClientId).HasColumnName("bc_client_id").HasMaxLength(64);
        entity.Property(e => e.BcClientSecretEncrypted).HasColumnName("bc_client_secret_encrypted");
        entity.Property(e => e.BcClientSecretExpiresAt).HasColumnName("bc_client_secret_expires_at");
        // HasSentinel as above (#767). AllowAll is the zero value, so an insert
        // that sets it explicitly was indistinguishable from one that left it
        // alone. Harmless while the store default is also AllowAll; a bug the
        // moment either that default or the zero member changes.
        entity.Property(e => e.LocalLoginPolicy)
            .HasColumnName("local_login_policy").HasConversion<string>().HasMaxLength(32)
            .IsRequired().HasDefaultValue(ALDevToolbox.Domain.ValueObjects.LocalLoginPolicy.AllowAll)
            .HasSentinel((ALDevToolbox.Domain.ValueObjects.LocalLoginPolicy)(-1));
        // GitHub App connection. A null installation id is "not connected" and
        // is the master switch for every GitHub feature, so the other three
        // columns are only meaningful alongside it.
        entity.Property(e => e.GitHubInstallationId).HasColumnName("github_installation_id");
        entity.Property(e => e.GitHubOrgLogin).HasColumnName("github_org_login").HasMaxLength(120);
        // jsonb rather than text so Postgres rejects a malformed blob at write
        // time; the shape is a flat permission -> read|write object.
        entity.Property(e => e.GitHubInstallationPermissions)
            .HasColumnName("github_installation_permissions").HasColumnType("jsonb");
        entity.Property(e => e.GitHubConnectedAt).HasColumnName("github_connected_at");
        // Repository standards, the ruleset half (#628). Same jsonb + value
        // converter shape as defaults_json on runtime_templates; nullable
        // because "no ruleset configured" has to stay distinguishable from an
        // empty one.
        entity.Property(e => e.GitHubRepositoryRuleset)
            .HasColumnName("github_repository_ruleset_json")
            .HasColumnType("jsonb")
            .HasConversion(rulesetConverter);
        // IANA zone id (issue #942). 64 is well above the longest id in the tz
        // database ("America/Argentina/ComodRivadavia", 32).
        entity.Property(e => e.DisplayTimeZoneId).HasColumnName("display_time_zone_id").HasMaxLength(64);
        // Default delivery windows for newly discovered environments (issue #962).
        entity.Property(e => e.DefaultDeliveryWindowProductionStart).HasColumnName("default_delivery_window_production_start");
        entity.Property(e => e.DefaultDeliveryWindowProductionEnd).HasColumnName("default_delivery_window_production_end");
        entity.Property(e => e.DefaultDeliveryWindowSandboxStart).HasColumnName("default_delivery_window_sandbox_start");
        entity.Property(e => e.DefaultDeliveryWindowSandboxEnd).HasColumnName("default_delivery_window_sandbox_end");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.HasIndex(e => e.OrganizationId).IsUnique();
        entity.HasOne(e => e.Organization)
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
