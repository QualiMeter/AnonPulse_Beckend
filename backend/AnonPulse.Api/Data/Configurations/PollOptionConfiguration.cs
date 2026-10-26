using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AnonPulse.Api.Data.Configurations;

public sealed class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
    public void Configure(EntityTypeBuilder<PollOption> builder)
    {
        builder.ToTable("poll_options", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "ck_poll_options_text_not_blank",
                "btrim(text) <> ''");
            tableBuilder.HasCheckConstraint(
                "ck_poll_options_position_nonnegative",
                "position >= 0");
        });

        builder.HasKey(option => option.Id);
        builder.HasAlternateKey(option => new { option.QuestionId, option.Id })
            .HasName("ak_poll_options_question_id_id");

        builder.Property(option => option.Id).HasColumnName("id");
        builder.Property(option => option.QuestionId).HasColumnName("question_id");
        builder.Property(option => option.Text)
            .HasColumnName("text")
            .HasColumnType("text")
            .IsRequired();
        builder.Property(option => option.Position).HasColumnName("position");

        builder.HasIndex(option => option.QuestionId);
        builder.HasIndex(option => new { option.QuestionId, option.Position })
            .IsUnique()
            .HasDatabaseName("ux_poll_options_question_id_position");

        builder.HasOne(option => option.Question)
            .WithMany(question => question.Options)
            .HasForeignKey(option => option.QuestionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
