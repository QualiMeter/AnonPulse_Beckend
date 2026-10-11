using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
    public void Configure(EntityTypeBuilder<PollOption> builder)
    {
        builder.ToTable("poll_options");

        builder.HasKey(option => option.Id);

        builder.Property(option => option.Id).HasColumnName("id");
        builder.Property(option => option.PollId).HasColumnName("poll_id");
        builder.Property(option => option.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(option => option.Position).HasColumnName("position");

        builder.HasOne(option => option.Poll)
            .WithMany(poll => poll.Options)
            .HasForeignKey(option => option.PollId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
