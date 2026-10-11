using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class DecisionConfiguration : IEntityTypeConfiguration<Decision>
{
    public void Configure(EntityTypeBuilder<Decision> builder)
    {
        builder.ToTable("decisions");

        builder.HasKey(decision => decision.Id);

        builder.Property(decision => decision.Id).HasColumnName("id");
        builder.Property(decision => decision.PollId).HasColumnName("poll_id");
        builder.Property(decision => decision.AuthorId).HasColumnName("author_id");
        builder.Property(decision => decision.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(decision => decision.PublishedAt)
            .HasColumnName("published_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(decision => decision.PollId)
            .IsUnique()
            .HasDatabaseName("ux_decisions_poll_id");

        builder.HasOne(decision => decision.Poll)
            .WithOne(poll => poll.Decision)
            .HasForeignKey<Decision>(decision => decision.PollId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(decision => decision.Author)
            .WithMany(user => user.Decisions)
            .HasForeignKey(decision => decision.AuthorId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
