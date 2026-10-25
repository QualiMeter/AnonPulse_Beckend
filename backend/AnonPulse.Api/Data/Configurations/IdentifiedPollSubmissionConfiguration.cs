using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class IdentifiedPollSubmissionConfiguration
    : IEntityTypeConfiguration<IdentifiedPollSubmission>
{
    public void Configure(EntityTypeBuilder<IdentifiedPollSubmission> builder)
    {
        builder.ToTable("identified_poll_submissions", table =>
            table.HasCheckConstraint(
                "ck_identified_poll_submissions_respondent_name_not_blank",
                "btrim(respondent_name) <> ''"));
        builder.HasKey(submission => submission.Id);
        builder.Property(submission => submission.Id).HasColumnName("id");
        builder.Property(submission => submission.PollId).HasColumnName("poll_id");
        builder.Property(submission => submission.RespondentName)
            .HasColumnName("respondent_name")
            .HasMaxLength(150)
            .IsRequired();
        builder.HasIndex(submission => submission.PollId);
        builder.HasOne(submission => submission.Poll)
            .WithMany(poll => poll.IdentifiedSubmissions)
            .HasForeignKey(submission => submission.PollId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_identified_poll_submissions_poll");
    }
}
