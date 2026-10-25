using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollTextAnswerConfiguration : IEntityTypeConfiguration<PollTextAnswer>
{
    public void Configure(EntityTypeBuilder<PollTextAnswer> builder)
    {
        builder.ToTable("poll_text_answers", table => table.HasCheckConstraint(
            "ck_poll_text_answers_text_not_blank", "btrim(text) <> ''"));
        builder.HasKey(answer => answer.Id);
        builder.Property(answer => answer.Id).HasColumnName("id");
        builder.Property(answer => answer.QuestionId).HasColumnName("question_id");
        builder.Property(answer => answer.Text).HasColumnName("text").HasMaxLength(4000).IsRequired();
        builder.Property(answer => answer.IdentifiedSubmissionId)
            .HasColumnName("identified_submission_id");
        builder.HasIndex(answer => answer.QuestionId);
        builder.HasIndex(answer => answer.IdentifiedSubmissionId);
        builder.HasOne(answer => answer.Question).WithMany(question => question.TextAnswers)
            .HasForeignKey(answer => answer.QuestionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(answer => answer.IdentifiedSubmission)
            .WithMany(submission => submission.TextAnswers)
            .HasForeignKey(answer => answer.IdentifiedSubmissionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_poll_text_answers_identified_submission");
    }
}
