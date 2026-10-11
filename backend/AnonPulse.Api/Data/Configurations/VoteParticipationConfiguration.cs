using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class VoteParticipationConfiguration
    : IEntityTypeConfiguration<VoteParticipation>
{
    public void Configure(EntityTypeBuilder<VoteParticipation> builder)
    {
        builder.ToTable("vote_participations", tableBuilder =>
            tableBuilder.HasCheckConstraint(
                "ck_vote_participations_token_hash_length",
                "length(token_hash) = 64"));

        builder.HasKey(participation => participation.Id);

        builder.Property(participation => participation.Id).HasColumnName("id");
        builder.Property(participation => participation.PollId).HasColumnName("poll_id");
        builder.Property(participation => participation.TokenHash)
            .HasColumnName("token_hash")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(participation => participation.UsedAt)
            .HasColumnName("used_at")
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(participation => new
        {
            participation.PollId,
            participation.TokenHash
        })
            .IsUnique()
            .HasDatabaseName("ux_vote_participations_poll_id_token_hash");

        builder.HasOne(participation => participation.Poll)
            .WithMany(poll => poll.VoteParticipations)
            .HasForeignKey(participation => participation.PollId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
