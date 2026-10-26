using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class NamedAttemptConfiguration : IEntityTypeConfiguration<NamedAttempt>
{
    public void Configure(EntityTypeBuilder<NamedAttempt> builder)
    {
        builder.ToTable("named_attempts", table =>
        {
            table.HasCheckConstraint("ck_named_attempts_name", "btrim(respondent_name) <> ''");
            table.HasCheckConstraint("ck_named_attempts_group", "btrim(respondent_group) <> ''");
            table.HasCheckConstraint("ck_named_attempts_request_hash", "length(request_hash) = 64");
            table.HasCheckConstraint("ck_named_attempts_secret_hash", "length(access_secret_hash) = 64");
            table.HasCheckConstraint("ck_named_attempts_state", "state IN ('completed')");
            table.HasCheckConstraint("ck_named_attempts_scores",
                "raw_score >= 0 AND maximum_score > 0 AND raw_score <= maximum_score AND " +
                "(normalized_score IS NULL OR normalized_score >= 0)");
            table.HasCheckConstraint("ck_named_attempts_not_final", "is_final = false");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.PollId).HasColumnName("poll_id");
        builder.Property(item => item.RespondentName).HasColumnName("respondent_name").HasMaxLength(150);
        builder.Property(item => item.RespondentGroup).HasColumnName("respondent_group").HasMaxLength(100);
        builder.Property(item => item.ClientRequestId).HasColumnName("client_request_id");
        builder.Property(item => item.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        builder.Property(item => item.AccessSecretHash).HasColumnName("access_secret_hash").HasMaxLength(64);
        builder.Property(item => item.State).HasColumnName("state").HasMaxLength(16);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.RawScore).HasColumnName("raw_score").HasColumnType("numeric(10,2)");
        builder.Property(item => item.MaximumScore).HasColumnName("maximum_score").HasColumnType("numeric(10,2)");
        builder.Property(item => item.NormalizedScore).HasColumnName("normalized_score").HasColumnType("numeric(10,2)");
        builder.Property(item => item.IsFullyGraded).HasColumnName("is_fully_graded");
        builder.Property(item => item.IsFinal).HasColumnName("is_final").HasDefaultValue(false);
        builder.HasIndex(item => new { item.PollId, item.ClientRequestId })
            .IsUnique().HasDatabaseName("ux_named_attempts_poll_id_client_request_id");
        builder.HasIndex(item => item.PollId);
        builder.HasOne(item => item.Poll).WithMany(poll => poll.NamedAttempts)
            .HasForeignKey(item => item.PollId).OnDelete(DeleteBehavior.Cascade);
    }
}
