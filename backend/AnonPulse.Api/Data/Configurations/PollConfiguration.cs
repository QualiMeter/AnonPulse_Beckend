using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollConfiguration : IEntityTypeConfiguration<Poll>
{
    public void Configure(EntityTypeBuilder<Poll> builder)
    {
        builder.ToTable("polls", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_polls_status",
                "status IN ('draft', 'open', 'closed')");
            tableBuilder.HasCheckConstraint(
                "ck_polls_response_mode",
                "response_mode IN ('anonymous', 'identified')");
        });

        builder.HasKey(poll => poll.Id);

        builder.Property(poll => poll.Id).HasColumnName("id");
        builder.Property(poll => poll.CreatorId).HasColumnName("creator_id");
        builder.Property(poll => poll.Title)
            .HasColumnName("title")
            .HasColumnType("text");
        builder.Property(poll => poll.Status)
            .HasColumnName("status")
            .HasMaxLength(6)
            .HasConversion(new ValueConverter<PollStatus, string>(
                status => status.ToString().ToLowerInvariant(),
                value => Enum.Parse<PollStatus>(value, true)));
        builder.Property(poll => poll.ResponseMode)
            .HasColumnName("response_mode")
            .HasMaxLength(16)
            .HasDefaultValue(PollResponseMode.Anonymous)
            .HasConversion(new ValueConverter<PollResponseMode, string>(
                mode => mode.ToApiValue(),
                value => Enum.Parse<PollResponseMode>(value, true)));
        builder.Property(poll => poll.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(poll => poll.PublishedAt)
            .HasColumnName("published_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(poll => poll.ClosedAt)
            .HasColumnName("closed_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(poll => poll.Version)
            .HasColumnName("xmin")
            .IsRowVersion();

        builder.HasOne(poll => poll.Creator)
            .WithMany(user => user.Polls)
            .HasForeignKey(poll => poll.CreatorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
