using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class TestGradingMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261026090737_AddKnowledgeGrading";
    private const string CurrentMigration = "20261027031338_FinalizeTestGrades";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_test_grading_migration")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task FilledDatabaseMigrationPreservesScores_MatchesScriptAndCascades()
    {
        DbContextOptions<ApplicationDbContext> options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options;
        Guid owner = Guid.NewGuid();
        Guid poll = Guid.NewGuid();
        Guid knowledgeQuestion = Guid.NewGuid();
        Guid choiceQuestion = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        Guid pendingAnswer = Guid.NewGuid();
        Guid autoAnswer = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        string manualScript = await File.ReadAllTextAsync(FindScript());

        await using (var db = new ApplicationDbContext(options))
        {
            IMigrator migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({owner}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls
                    (id, creator_id, title, status, response_mode, kind, test_scale, created_at)
                VALUES ({poll}, {owner}, 'Preserved test', 'open', 'identified', 'test', 10,
                        {now});
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers, question_type,
                     text_purpose, test_weight)
                VALUES
                    ({knowledgeQuestion}, {poll}, 'Explain', 0, FALSE, 'text', 'knowledge', NULL),
                    ({choiceQuestion}, {poll}, 'Choose', 1, FALSE, 'single_choice', NULL, 2);
                INSERT INTO poll_question_knowledge_settings
                    (question_id, expected_answer, grading_criteria, max_score)
                VALUES ({knowledgeQuestion}, 'Expected', 'Rubric', 3);
                INSERT INTO named_attempts
                    (id, poll_id, respondent_name, respondent_group, client_request_id,
                     request_hash, access_secret_hash, state, created_at, completed_at,
                     raw_score, maximum_score, normalized_score, is_fully_graded, is_final)
                VALUES ({attempt}, {poll}, 'Student', 'G', {Guid.NewGuid()},
                        {new string('a', 64)}, {new string('b', 64)}, 'completed', {now}, {now},
                        2, 5, NULL, FALSE, FALSE);
                INSERT INTO named_attempt_answers
                    (id, attempt_id, question_id, text, grading_state, score, maximum_score)
                VALUES
                    ({pendingAnswer}, {attempt}, {knowledgeQuestion}, 'Answer', 'pending', NULL, 3),
                    ({autoAnswer}, {attempt}, {choiceQuestion}, NULL, 'auto_graded', 2, 2);
                UPDATE polls SET status = 'closed', closed_at = {now} WHERE id = {poll};
                """);
            string generated = migrator.GenerateScript(PreviousMigration, CurrentMigration,
                MigrationsSqlGenerationOptions.Idempotent);
            Assert.Equal(Normalize(generated), Normalize(manualScript));
            await db.Database.ExecuteSqlRawAsync(manualScript);
            await db.Database.ExecuteSqlRawAsync(manualScript);
        }

        await using (var verify = new ApplicationDbContext(options))
        {
            NamedAttempt preserved = await verify.NamedAttempts.AsNoTracking()
                .Include(item => item.Answers)
                .SingleAsync(item => item.Id == attempt);
            Assert.False(preserved.IsFinal);
            Assert.Null(preserved.FinalizedAt);
            Assert.Null(preserved.FinalizedByUserId);
            Assert.Contains(preserved.Answers, answer =>
                answer.Id == pendingAnswer && answer.GradingState == "pending" &&
                answer.Score == null);
            Assert.Contains(preserved.Answers, answer =>
                answer.Id == autoAnswer && answer.GradingState == "auto_graded" &&
                answer.Score == 2m);
            string[] indexes = await verify.Database.SqlQueryRaw<string>(
                    "SELECT indexname AS \"Value\" FROM pg_indexes " +
                    "WHERE tablename = 'test_grade_changes' ORDER BY indexname")
                .ToArrayAsync();
            Assert.Contains("ix_test_grade_changes_attempt_changed_at", indexes);
            Assert.Contains("ix_test_grade_changes_answer_id", indexes);

            await Assert.ThrowsAsync<PostgresException>(() =>
                verify.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE named_attempt_answers SET score = 1
                    WHERE id = {pendingAnswer}
                    """));
            await verify.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE named_attempt_answers
                SET grading_state = 'teacher_graded', score = 2.50
                WHERE id = {pendingAnswer};
                INSERT INTO test_grade_changes
                    (id, attempt_id, answer_id, actor_id, old_score, new_score, source,
                     knowledge_grading_suggestion_id, changed_at)
                VALUES ({Guid.NewGuid()}, {attempt}, {pendingAnswer}, {owner}, NULL, 2.50,
                        'manual', NULL, {now});
                UPDATE named_attempts
                SET raw_score = 4.50, normalized_score = 9.00, is_fully_graded = TRUE,
                    is_final = TRUE, finalized_by_user_id = {owner}, finalized_at = {now}
                WHERE id = {attempt};
                """);
        }

        await using (var deleteDb = new ApplicationDbContext(options))
        {
            PollDeletionResult result = await new PollDeletionService(
                deleteDb, TimeProvider.System).DeleteAdminAsync(owner, poll, true);
            Assert.Equal(PollDeletionOutcome.Succeeded, result.Outcome);
        }
        await using var afterDelete = new ApplicationDbContext(options);
        Assert.False(await afterDelete.NamedAttempts.AnyAsync(item => item.Id == attempt));
        Assert.Empty(await afterDelete.TestGradeChanges.ToArrayAsync());
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "scripts", "migrations",
                "20261027031338_finalize_test_grades.sql");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Test grading migration script was not found.");
    }

    private static string Normalize(string sql) => sql.TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
