using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class OpenTextQuestionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261023064551_AddAdministrationAndAudit";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_text_migration_tests")
        .WithUsername("anonpulse_tests").WithPassword(Guid.NewGuid().ToString("N")).Build();
    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_SupportsLegacyAndExplicitWritersWithoutLosingData()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        Guid user = Guid.NewGuid(), poll = Guid.NewGuid();
        Guid single = Guid.NewGuid(), multiple = Guid.NewGuid(), option = Guid.NewGuid(), vote = Guid.NewGuid();
        await using (var context = new ApplicationDbContext(options))
        {
            IMigrator migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({user}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls (id, creator_id, status, created_at) VALUES ({poll}, {user}, 'closed', {DateTime.UtcNow});
                INSERT INTO poll_questions (id, poll_id, text, position, allows_multiple_answers)
                VALUES ({single}, {poll}, 'Single', 0, FALSE), ({multiple}, {poll}, 'Multiple', 1, TRUE);
                INSERT INTO poll_options (id, question_id, text, position) VALUES ({option}, {single}, 'A', 0);
                INSERT INTO votes (id, option_id, created_at) VALUES ({vote}, {option}, NULL);
                """);
            await migrator.MigrateAsync();
        }

        Guid legacySingle = Guid.NewGuid(), legacyMultiple = Guid.NewGuid(), legacyUpdate = Guid.NewGuid();
        var legacyOptions = new DbContextOptionsBuilder<LegacyPollQuestionWriterContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var legacy = new LegacyPollQuestionWriterContext(legacyOptions))
        {
            legacy.Questions.AddRange(
                new LegacyPollQuestion
                {
                    Id = legacySingle,
                    PollId = poll,
                    Text = "Legacy single",
                    Position = 2,
                    AllowsMultipleAnswers = false
                },
                new LegacyPollQuestion
                {
                    Id = legacyMultiple,
                    PollId = poll,
                    Text = "Legacy multiple",
                    Position = 3,
                    AllowsMultipleAnswers = true
                },
                new LegacyPollQuestion
                {
                    Id = legacyUpdate,
                    PollId = poll,
                    Text = "Legacy update",
                    Position = 4,
                    AllowsMultipleAnswers = false
                });
            await legacy.SaveChangesAsync();

            LegacyPollQuestion update = await legacy.Questions.SingleAsync(q => q.Id == legacyUpdate);
            update.AllowsMultipleAnswers = true;
            await legacy.SaveChangesAsync();
        }

        Guid explicitSingle = Guid.NewGuid(), explicitMultiple = Guid.NewGuid();
        Guid feedback = Guid.NewGuid(), knowledge = Guid.NewGuid();
        await using (var current = new ApplicationDbContext(options))
        {
            var knowledgeQuestion = new PollQuestion
            {
                Id = knowledge,
                PollId = poll,
                Text = "Knowledge",
                Position = 8,
                Type = PollQuestionType.Text,
                TextPurpose = TextQuestionPurpose.Knowledge
            };
            knowledgeQuestion.KnowledgeSettings = new PollQuestionKnowledgeSettings
            {
                QuestionId = knowledge,
                ExpectedAnswer = "Expected",
                GradingCriteria = "Criteria",
                MaxScore = 100
            };
            current.PollQuestions.AddRange(
                new PollQuestion
                {
                    Id = explicitSingle,
                    PollId = poll,
                    Text = "Explicit single",
                    Position = 5,
                    Type = PollQuestionType.SingleChoice
                },
                new PollQuestion
                {
                    Id = explicitMultiple,
                    PollId = poll,
                    Text = "Explicit multiple",
                    Position = 6,
                    Type = PollQuestionType.MultipleChoice
                },
                new PollQuestion
                {
                    Id = feedback,
                    PollId = poll,
                    Text = "Feedback",
                    Position = 7,
                    Type = PollQuestionType.Text,
                    TextPurpose = TextQuestionPurpose.Feedback
                },
                knowledgeQuestion);
            await current.SaveChangesAsync();
        }

        await using var verify = new ApplicationDbContext(options);
        Dictionary<Guid, PollQuestion> questions = await verify.PollQuestions
            .AsNoTracking()
            .ToDictionaryAsync(q => q.Id);
        Assert.Equal(PollQuestionType.SingleChoice, questions[single].Type);
        Assert.Equal(PollQuestionType.MultipleChoice, questions[multiple].Type);
        Assert.Equal(PollQuestionType.SingleChoice, questions[legacySingle].Type);
        Assert.Equal(PollQuestionType.MultipleChoice, questions[legacyMultiple].Type);
        Assert.Equal(PollQuestionType.MultipleChoice, questions[legacyUpdate].Type);
        Assert.Equal(PollQuestionType.SingleChoice, questions[explicitSingle].Type);
        Assert.Equal(PollQuestionType.MultipleChoice, questions[explicitMultiple].Type);
        Assert.Equal(TextQuestionPurpose.Feedback, questions[feedback].TextPurpose);
        Assert.Equal(TextQuestionPurpose.Knowledge, questions[knowledge].TextPurpose);
        Assert.True(await verify.Votes.AnyAsync(v => v.Id == vote));
        Assert.Empty(verify.PollTextAnswers);
        Assert.Equal(100, (await verify.PollQuestionKnowledgeSettings
            .SingleAsync(settings => settings.QuestionId == knowledge)).MaxScore);

        PostgresException insertConflict = await Assert.ThrowsAsync<PostgresException>(() =>
            verify.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers, question_type)
                VALUES ({Guid.NewGuid()}, {poll}, 'Conflict', 9, TRUE, 'single_choice')
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, insertConflict.SqlState);

        PostgresException updateConflict = await Assert.ThrowsAsync<PostgresException>(() =>
            verify.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE poll_questions
                SET allows_multiple_answers = TRUE, question_type = 'single_choice'
                WHERE id = {explicitSingle}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, updateConflict.SqlState);

        string explicitSingleState = await verify.Database.SqlQuery<string>($"""
            SELECT question_type || '|' || allows_multiple_answers::text AS "Value"
            FROM poll_questions
            WHERE id = {explicitSingle}
            """).SingleAsync();
        Assert.Equal("single_choice|false", explicitSingleState);

        int textMaximumLength = await verify.Database.SqlQueryRaw<int>("""
            SELECT character_maximum_length::integer AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'poll_text_answers'
              AND column_name = 'text'
            """).SingleAsync();
        string maxScoreConstraint = await verify.Database.SqlQueryRaw<string>("""
            SELECT pg_get_constraintdef(oid) AS "Value"
            FROM pg_constraint
            WHERE conname = 'ck_poll_question_knowledge_settings_max_score'
            """).SingleAsync();
        string questionTypeColumn = await verify.Database.SqlQueryRaw<string>("""
            SELECT is_nullable || '|' || coalesce(column_default, '<none>') AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'poll_questions'
              AND column_name = 'question_type'
            """).SingleAsync();
        int compatibilityTriggerCount = await verify.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value"
            FROM pg_trigger
            WHERE tgrelid = 'poll_questions'::regclass
              AND NOT tgisinternal
              AND tgname LIKE 'trg_poll_questions_%'
            """).SingleAsync();
        Assert.Equal(4000, textMaximumLength);
        Assert.Contains("100", maxScoreConstraint, StringComparison.Ordinal);
        Assert.Equal("NO|<none>", questionTypeColumn);
        Assert.Equal(3, compatibilityTriggerCount);

        IMigrator downMigrator = verify.Database.GetService<IMigrator>();
        await downMigrator.MigrateAsync(PreviousMigration);
        int compatibilityFunctionCount = await verify.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value"
            FROM pg_proc
            WHERE proname = 'set_poll_question_type_for_legacy_writes'
            """).SingleAsync();
        int questionTypeColumnCount = await verify.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'poll_questions'
              AND column_name = 'question_type'
            """).SingleAsync();
        int preservedVoteCount = await verify.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value" FROM votes WHERE id = {vote}
            """).SingleAsync();
        Assert.Equal(0, compatibilityFunctionCount);
        Assert.Equal(0, questionTypeColumnCount);
        Assert.Equal(1, preservedVoteCount);
    }

    private sealed class LegacyPollQuestionWriterContext(
        DbContextOptions<LegacyPollQuestionWriterContext> options) : DbContext(options)
    {
        public DbSet<LegacyPollQuestion> Questions => Set<LegacyPollQuestion>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LegacyPollQuestion>(builder =>
            {
                builder.ToTable("poll_questions");
                builder.HasKey(question => question.Id);
                builder.Property(question => question.Id).HasColumnName("id");
                builder.Property(question => question.PollId).HasColumnName("poll_id");
                builder.Property(question => question.Text).HasColumnName("text").IsRequired();
                builder.Property(question => question.Position).HasColumnName("position");
                builder.Property(question => question.AllowsMultipleAnswers)
                    .HasColumnName("allows_multiple_answers")
                    .HasDefaultValue(false)
                    .IsRequired();
            });
        }
    }

    private sealed class LegacyPollQuestion
    {
        public Guid Id { get; set; }
        public Guid PollId { get; set; }
        public required string Text { get; set; }
        public int Position { get; set; }
        public bool AllowsMultipleAnswers { get; set; }
    }
}
