using AnonPulse.Api.Data;
using AnonPulse.Api.Services.Admin;
using AnonPulse.Api.Services.FeedbackAnalysis;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.KnowledgeGrading;

public sealed class KnowledgeGradingMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261026043614_AddTestModeAndNamedAttempts";
    private const string CurrentMigration = "20261026081034_AddKnowledgeGrading";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_knowledge_grading_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N")).Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task FilledDatabaseMigrationMatchesScript_QueueIsAtomic_AndDeleteCascades()
    {
        DbContextOptions<ApplicationDbContext> dbOptions =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options;
        Guid owner = Guid.NewGuid(), poll = Guid.NewGuid(), question = Guid.NewGuid();
        Guid attempt = Guid.NewGuid(), answer = Guid.NewGuid();
        await using (var migrationDb = new ApplicationDbContext(dbOptions))
        {
            IMigrator migrator = migrationDb.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await migrationDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({owner}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls
                    (id, creator_id, status, response_mode, kind, test_scale, created_at)
                VALUES ({poll}, {owner}, 'open', 'identified', 'test', 5, {DateTime.UtcNow});
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers, question_type,
                     text_purpose, test_weight)
                VALUES ({question}, {poll}, 'Что такое полиморфизм?', 0, FALSE, 'text',
                        'knowledge', NULL);
                INSERT INTO poll_question_knowledge_settings
                    (question_id, expected_answer, grading_criteria, max_score)
                VALUES ({question}, 'Общий интерфейс', 'Определение и пример', 5);
                INSERT INTO named_attempts
                    (id, poll_id, respondent_name, respondent_group, client_request_id,
                     request_hash, access_secret_hash, state, created_at, completed_at,
                     raw_score, maximum_score, normalized_score, is_fully_graded, is_final)
                VALUES ({attempt}, {poll}, 'Студент', 'A', {Guid.NewGuid()},
                        {new string('a', 64)}, {new string('b', 64)}, 'completed',
                        {DateTime.UtcNow}, {DateTime.UtcNow}, 0, 5, NULL, FALSE, FALSE);
                INSERT INTO named_attempt_answers
                    (id, attempt_id, question_id, text, grading_state, score, maximum_score)
                VALUES ({answer}, {attempt}, {question}, 'Другими словами: общий контракт',
                        'pending', NULL, 5);
                UPDATE polls SET status = 'closed', closed_at = {DateTime.UtcNow}
                WHERE id = {poll};
                """);
            string generated = migrator.GenerateScript(PreviousMigration, CurrentMigration,
                MigrationsSqlGenerationOptions.Idempotent);
            Assert.Equal(Normalize(generated), Normalize(await File.ReadAllTextAsync(FindScript())));
            await migrationDb.Database.ExecuteSqlRawAsync(generated);
        }

        var ai = Options.Create(new AiOptions
        {
            Enabled = true,
            KnowledgeGradingEnabled = true,
            Provider = "Groq",
            Model = "openai/gpt-oss-20b",
            KnowledgeMaxAnswers = 10,
            KnowledgeMaxTotalCharacters = 20_000,
            KnowledgeMaxInputTokens = 6_000,
            KnowledgeMaxOutputTokens = 2_000,
            KnowledgeMaxReservedTokens = 8_000,
            Groq = new GroqOptions { ApiKey = "fake" }
        });
        await using var firstDb = new ApplicationDbContext(dbOptions);
        await using var secondDb = new ApplicationDbContext(dbOptions);
        KnowledgeGradingServiceResult[] starts = await Task.WhenAll(
            new KnowledgeGradingService(firstDb, new PersonalDataSanitizer(), ai,
                TimeProvider.System).StartAsync(owner, false, poll, question, true, default),
            new KnowledgeGradingService(secondDb, new PersonalDataSanitizer(), ai,
                TimeProvider.System).StartAsync(owner, false, poll, question, true, default));
        Assert.Equal(starts[0].Job!.Id, starts[1].Job!.Id);

        await using var queueDb1 = new ApplicationDbContext(dbOptions);
        await using var queueDb2 = new ApplicationDbContext(dbOptions);
        Guid?[] claims = await Task.WhenAll(
            new KnowledgeGradingJobQueue(queueDb1, ai, TimeProvider.System)
                .ClaimNextAsync(default),
            new KnowledgeGradingJobQueue(queueDb2, ai, TimeProvider.System)
                .ClaimNextAsync(default));
        Assert.Single(claims, value => value is not null);

        Guid claimedJob = claims.Single(value => value is not null)!.Value;
        await using (var suggestionDb = new ApplicationDbContext(dbOptions))
        {
            await suggestionDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO poll_knowledge_grading_suggestions
                    (id, job_id, answer_id, suggested_score, max_score, explanation,
                     correct_points, missing_or_incorrect_points, needs_review,
                     review_reasons, created_at)
                VALUES ({Guid.NewGuid()}, {claimedJob}, {answer}, 4, 5, 'Обоснование',
                        '["Верный смысл"]'::jsonb, '["Нет примера"]'::jsonb,
                        FALSE, '[]'::jsonb, {DateTime.UtcNow});
                UPDATE poll_knowledge_grading_jobs SET suggested_count = 1
                WHERE id = {claimedJob};
                """);
        }

        await using (var deleteDb = new ApplicationDbContext(dbOptions))
        {
            PollDeletionResult deleted = await new PollDeletionService(deleteDb,
                TimeProvider.System).DeleteAdminAsync(owner, poll, true, default);
            Assert.Equal(PollDeletionOutcome.Succeeded, deleted.Outcome);
        }
        await using var verify = new ApplicationDbContext(dbOptions);
        Assert.Empty(verify.PollKnowledgeGradingJobs);
        Assert.Empty(verify.PollKnowledgeGradingSuggestions);
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "scripts", "migrations",
                "20261026081034_add_knowledge_grading.sql");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Knowledge grading migration script not found.");
    }

    private static string Normalize(string sql) => sql.TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
