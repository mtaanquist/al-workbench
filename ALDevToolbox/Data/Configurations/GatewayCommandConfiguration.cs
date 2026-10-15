using ALDevToolbox.Domain.Entities;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class GatewayCommandConfiguration : IEntityTypeConfiguration<GatewayCommand>
{
    public void Configure(EntityTypeBuilder<GatewayCommand> entity)
    {
        entity.ToTable("gateway_commands");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.GatewayId).HasColumnName("gateway_id").IsRequired();
        // The wire word ("publish-install"), so the column reads the same as the
        // protocol, and a value the enum no longer has fails loudly on read.
        entity.Property(e => e.Verb).HasColumnName("verb")
            .HasConversion(v => GatewayVerbs.ToWire(v), s => GatewayVerbs.Parse(s))
            .HasMaxLength(40)
            .IsRequired();
        entity.Property(e => e.ParametersJson).HasColumnName("parameters").HasColumnType("jsonb").IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        entity.Property(e => e.RequestedByUserId).HasColumnName("requested_by_user_id");
        entity.Property(e => e.StartedByAgent).HasColumnName("started_by_agent").IsRequired().HasDefaultValue(false);
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.DueAt).HasColumnName("due_at").IsRequired();
        entity.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
        entity.Property(e => e.ClaimedAt).HasColumnName("claimed_at");
        entity.Property(e => e.StartedAt).HasColumnName("started_at");
        entity.Property(e => e.LastReportedAt).HasColumnName("last_reported_at");
        entity.Property(e => e.LastProgressSequence).HasColumnName("last_progress_sequence").IsRequired();
        entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
        entity.Property(e => e.ResultJson).HasColumnName("result").HasColumnType("jsonb");
        entity.Property(e => e.FailureMessage).HasColumnName("failure_message");
        entity.Property(e => e.Log).HasColumnName("log").IsRequired().HasDefaultValue(string.Empty);

        // The long-poll's read: this gateway's queued commands, oldest due first.
        entity.HasIndex(e => new { e.GatewayId, e.Status, e.DueAt });
        // The sweep's reads: queued past their deadline, and claimed or running gone quiet.
        entity.HasIndex(e => new { e.OrganizationId, e.Status });

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.Gateway)
            .WithMany()
            .HasForeignKey(e => e.GatewayId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(e => e.RequestedByUser)
            .WithMany()
            .HasForeignKey(e => e.RequestedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasMany(e => e.Files)
            .WithOne(f => f.Command)
            .HasForeignKey(f => f.CommandId)
            .OnDelete(DeleteBehavior.Cascade);

        // Query filter installed in AppDbContext.OnModelCreating.
    }
}

internal sealed class GatewayCommandFileConfiguration : IEntityTypeConfiguration<GatewayCommandFile>
{
    public void Configure(EntityTypeBuilder<GatewayCommandFile> entity)
    {
        entity.ToTable("gateway_command_files");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id").IsRequired();
        entity.Property(e => e.CommandId).HasColumnName("command_id").IsRequired();
        entity.Property(e => e.FileName).HasColumnName("file_name").IsRequired();
        entity.Property(e => e.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
        entity.Property(e => e.Size).HasColumnName("size").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content");
        entity.Property(e => e.ProjectBuildArtifactId).HasColumnName("project_build_artifact_id");

        entity.HasIndex(e => e.CommandId);
        // Plain, not filtered: it covers the SET NULL below, which has to find every row.
        entity.HasIndex(e => e.ProjectBuildArtifactId);

        // A deployment's files point at the build's artifacts rather than copying them
        // (#1367). Deleting a build's artifacts leaves the command's record of what was
        // sent (name, hash, size) and only loses the bytes.
        entity.HasOne(e => e.ProjectBuildArtifact)
            .WithMany()
            .HasForeignKey(e => e.ProjectBuildArtifactId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(e => e.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
