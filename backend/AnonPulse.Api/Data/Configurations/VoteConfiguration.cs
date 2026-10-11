using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class VoteConfiguration : IEntityTypeConfiguration<Vote>
{
    public void Configure(EntityTypeBuilder<Vote> builder)
    {
        builder.ToTable("votes");

        builder.HasKey(vote => vote.Id);

        builder.Property(vote => vote.Id).HasColumnName("id");
        builder.Property(vote => vote.OptionId).HasColumnName("option_id");
        builder.Property(vote => vote.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone");

        builder.HasOne(vote => vote.Option)
            .WithMany(option => option.Votes)
            .HasForeignKey(vote => vote.OptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
