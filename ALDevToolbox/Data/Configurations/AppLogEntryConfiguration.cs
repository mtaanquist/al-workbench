using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> entity)
    {
        entity.ToTable("app_log_entries");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        entity.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
        entity.Property(e => e.Level).HasColumnName("level").IsRequired();
        entity.Property(e => e.Category).HasColumnName("category").IsRequired();
        entity.Property(e => e.Worker).HasColumnName("worker");
        entity.Property(e => e.Message).HasColumnName("message").IsRequired();
        entity.Property(e => e.Exception).HasColumnName("exception");
        // The retention sweep deletes by timestamp, and the page's "from" filter
        // reads a time range.
        entity.HasIndex(e => e.Timestamp);
        // The page pages newest first by id within one worker.
        entity.HasIndex(e => new { e.Worker, e.Id });
        // "Warnings and errors" and "Errors only": rare lines among millions, found
        // without walking every Information line newest first.
        entity.HasIndex(e => new { e.Level, e.Id });
    }
}
