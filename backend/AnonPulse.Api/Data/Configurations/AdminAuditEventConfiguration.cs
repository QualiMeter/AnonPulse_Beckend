using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class AdminAuditEventConfiguration
    : IEntityTypeConfiguration<AdminAuditEvent>
{
    public void Configure(EntityTypeBuilder<AdminAuditEvent> builder)
    {
        builder.ToTable("admin_audit_events");

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.AdministratorId)
            .HasColumnName("administrator_id");
        builder.Property(item => item.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(item => item.TargetType)
            .HasColumnName("target_type")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(item => item.TargetId).HasColumnName("target_id");
        builder.Property(item => item.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(item => item.MetadataJson)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(item => item.OccurredAt)
            .HasDatabaseName("ix_admin_audit_events_occurred_at");
        builder.HasIndex(item => item.AdministratorId)
            .HasDatabaseName("ix_admin_audit_events_administrator_id");
        builder.HasIndex(item => new { item.TargetType, item.TargetId })
            .HasDatabaseName("ix_admin_audit_events_target");

        // Deliberately no FK to users: audit history must survive later deletion
        // of an administrator account.
    }
}
