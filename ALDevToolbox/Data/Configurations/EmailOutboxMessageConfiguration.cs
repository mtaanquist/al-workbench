using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ALDevToolbox.Data.Configurations;

internal sealed class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> entity)
    {
        entity.ToTable("email_outbox");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.ToEmail).HasColumnName("to_email").IsRequired();
        entity.Property(e => e.Purpose)
            .HasColumnName("purpose").HasConversion<string>().HasMaxLength(64).IsRequired();
        entity.Property(e => e.Subject).HasColumnName("subject").IsRequired();
        entity.Property(e => e.BodyEncrypted).HasColumnName("body_encrypted");
        entity.Property(e => e.TextBodyEncrypted).HasColumnName("text_body_encrypted");
        entity.Property(e => e.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(e => e.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        entity.Property(e => e.LastError).HasColumnName("last_error");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
        entity.Property(e => e.SentAt).HasColumnName("sent_at");
        entity.Property(e => e.OrganizationId).HasColumnName("organization_id");

        // The drain's only query: the due Pending rows, oldest first.
        entity.HasIndex(e => new { e.Status, e.NextAttemptAt })
            .HasDatabaseName("ix_email_outbox_status_next_attempt");
        // The stale write-off and the given-up delete, both
        // `status = x AND created_at < cutoff`.
        entity.HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("ix_email_outbox_status_created");
        // The sent rows are read and deleted by when they were *sent*: the page's
        // "went out in the last 24 hours" count and the delete that keeps that
        // window's worth. The index above leads with created_at and does not
        // serve either.
        entity.HasIndex(e => new { e.Status, e.SentAt })
            .HasDatabaseName("ix_email_outbox_status_sent");

        // No foreign key to organizations: the column is a label for the
        // operator's list, not a relationship, and a deleted organisation must
        // not block pruning (or resurrect) a queued message. See the type remarks.
        //
        // No organization_id query filter either: this table is written before
        // an account exists and read by a cross-org console, so it sits outside
        // the tenant fence like login_attempts and pending_signups. With no
        // filter to escape, its reads carry no IgnoreQueryFilters() bypass.
    }
}
