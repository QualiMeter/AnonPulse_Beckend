using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class IdentifiedPollMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261024163743_AddOpenTextQuestions";
    private const string CurrentMigration = "20261025052103_AddIdentifiedPolls";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_identified_migration_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_FromPr17_PreservesAnonymousDataAndCompatibilityTriggers()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        Guid user = Guid.NewGuid();
        Guid poll = Guid.NewGuid();
        Guid question = Guid.NewGuid();
        Guid option = Guid.NewGuid();
        Guid vote = Guid.NewGuid();
        Guid textQuestion = Guid.NewGuid();
        Guid textAnswer = Guid.NewGuid();

        await using (var context = new ApplicationDbContext(options))
        {
            IMigrator migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({user}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls (id, creator_id, status, created_at)
                VALUES ({poll}, {user}, 'closed', {DateTime.UtcNow});
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers, question_type,
                     text_purpose)
                VALUES
                    ({question}, {poll}, 'Choice', 0, FALSE, 'single_choice', NULL),
                    ({textQuestion}, {poll}, 'Text', 1, FALSE, 'text', 'feedback');
                INSERT INTO poll_options (id, question_id, text, position)
                VALUES ({option}, {question}, 'A', 0);
                INSERT INTO votes (id, option_id, created_at)
                VALUES ({vote}, {option}, NULL);
                INSERT INTO poll_text_answers (id, question_id, text)
                VALUES ({textAnswer}, {textQuestion}, 'legacy anonymous text');
                """);
            string manualScript = await File.ReadAllTextAsync(FindManualScript());
            string generatedScript = migrator.GenerateScript(
                PreviousMigration,
                CurrentMigration,
                MigrationsSqlGenerationOptions.Idempotent);
            Assert.Equal(NormalizeSql(generatedScript), NormalizeSql(manualScript));
            await context.Database.ExecuteSqlRawAsync(manualScript);
        }

        await using var verify = new ApplicationDbContext(options);
        Poll migratedPoll = await verify.Polls.AsNoTracking()
            .SingleAsync(item => item.Id == poll);
        Vote migratedVote = await verify.Votes.AsNoTracking()
            .SingleAsync(item => item.Id == vote);
        PollTextAnswer migratedText = await verify.PollTextAnswers.AsNoTracking()
            .SingleAsync(item => item.Id == textAnswer);
        int compatibilityTriggerCount = await verify.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value"
            FROM pg_trigger
            WHERE tgrelid = 'poll_questions'::regclass
              AND NOT tgisinternal
              AND tgname LIKE 'trg_poll_questions_%'
            """).SingleAsync();

        Assert.Equal(PollResponseMode.Anonymous, migratedPoll.ResponseMode);
        Assert.Null(migratedVote.IdentifiedSubmissionId);
        Assert.Null(migratedText.IdentifiedSubmissionId);
        Assert.Empty(verify.IdentifiedPollSubmissions);
        Assert.Equal(3, compatibilityTriggerCount);
    }

    [Fact]
    public async Task Migration_EnforcesModeImmutabilityAndSubmissionPollIntegrity()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.MigrateAsync();
        Guid user = Guid.NewGuid();
        Guid anonymousPoll = Guid.NewGuid();
        Guid identifiedPoll = Guid.NewGuid();
        Guid otherPoll = Guid.NewGuid();
        Guid submission = Guid.NewGuid();
        Guid option = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users
                ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                 "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
            VALUES ({user}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
            INSERT INTO polls (id, creator_id, status, response_mode, created_at)
            VALUES
                ({anonymousPoll}, {user}, 'draft', 'anonymous', {DateTime.UtcNow}),
                ({identifiedPoll}, {user}, 'closed', 'identified', {DateTime.UtcNow}),
                ({otherPoll}, {user}, 'closed', 'identified', {DateTime.UtcNow});
            INSERT INTO identified_poll_submissions (id, poll_id, respondent_name)
            VALUES ({submission}, {identifiedPoll}, 'Name');
            INSERT INTO poll_questions
                (id, poll_id, text, position, allows_multiple_answers, question_type)
            VALUES ({Guid.NewGuid()}, {otherPoll}, 'Other', 0, FALSE, 'single_choice');
            """);
        Guid otherQuestion = await context.PollQuestions
            .Where(question => question.PollId == otherPoll)
            .Select(question => question.Id)
            .SingleAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO poll_options (id, question_id, text, position)
            VALUES ({option}, {otherQuestion}, 'A', 0);
            """);

        PostgresException anonymousSubmission = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO identified_poll_submissions (id, poll_id, respondent_name)
                VALUES ({Guid.NewGuid()}, {anonymousPoll}, 'Forbidden');
                """));
        PostgresException publishedModeChange = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE polls SET response_mode = 'anonymous' WHERE id = {identifiedPoll};
                """));
        PostgresException crossPollAnswer = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO votes (id, option_id, identified_submission_id)
                VALUES ({Guid.NewGuid()}, {option}, {submission});
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, anonymousSubmission.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, publishedModeChange.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, crossPollAnswer.SqlState);
    }

    private static string FindManualScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "scripts",
                "migrations",
                "20261025052103_add_identified_polls.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The identified polls manual migration script was not found.");
    }

    private static string NormalizeSql(string sql) =>
        sql.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
