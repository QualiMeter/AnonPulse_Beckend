using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollQuestionConfiguration : IEntityTypeConfiguration<PollQuestion>
{
    public void Configure(EntityTypeBuilder<PollQuestion> builder)
    {
        builder.ToTable("poll_questions", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_poll_questions_text_not_blank",
                "btrim(text) <> ''");
            tableBuilder.HasCheckConstraint(
                "ck_poll_questions_position_nonnegative",
                "position >= 0");
            tableBuilder.HasCheckConstraint(
                "ck_poll_questions_type",
                "question_type IN ('single_choice', 'multiple_choice', 'text')");
            tableBuilder.HasCheckConstraint(
                "ck_poll_questions_type_legacy_consistent",
                "(question_type = 'multiple_choice') = allows_multiple_answers");
            tableBuilder.HasCheckConstraint(
                "ck_poll_questions_text_purpose",
                "(question_type = 'text' AND text_purpose IN ('feedback', 'knowledge')) OR " +
                "(question_type <> 'text' AND text_purpose IS NULL)");
        });

        builder.HasKey(question => question.Id);

        builder.Property(question => question.Id).HasColumnName("id");
        builder.Property(question => question.PollId).HasColumnName("poll_id");
        builder.Property(question => question.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(question => question.Position).HasColumnName("position");
        builder.Property(question => question.AllowsMultipleAnswers)
            .HasColumnName("allows_multiple_answers")
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(question => question.Type)
            .HasColumnName("question_type")
            .HasConversion(
                value => value.ToApiValue(),
                value => value == "multiple_choice" ? PollQuestionType.MultipleChoice :
                    value == "text" ? PollQuestionType.Text : PollQuestionType.SingleChoice)
            .HasMaxLength(32)
            .HasDefaultValue(PollQuestionType.SingleChoice)
            .IsRequired();
        builder.Property(question => question.TextPurpose)
            .HasColumnName("text_purpose")
            .HasConversion(
                value => value == TextQuestionPurpose.Feedback ? "feedback" :
                    value == TextQuestionPurpose.Knowledge ? "knowledge" : null,
                value => value == "feedback" ? TextQuestionPurpose.Feedback :
                    value == "knowledge" ? TextQuestionPurpose.Knowledge : null)
            .HasMaxLength(16);

        builder.HasIndex(question => question.PollId);
        builder.HasIndex(question => new { question.PollId, question.Position })
            .IsUnique()
            .HasDatabaseName("ux_poll_questions_poll_id_position");

        builder.HasOne(question => question.Poll)
            .WithMany(poll => poll.Questions)
            .HasForeignKey(question => question.PollId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
