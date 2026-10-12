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
        });

        builder.HasKey(question => question.Id);

        builder.Property(question => question.Id).HasColumnName("id");
        builder.Property(question => question.PollId).HasColumnName("poll_id");
        builder.Property(question => question.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(question => question.Position).HasColumnName("position");

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
