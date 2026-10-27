using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Admin;
using AnonPulse.Api.Services.FeedbackAnalysis;
using AnonPulse.Api.Services.KnowledgeGrading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.KnowledgeGrading;

public sealed class KnowledgeGradingMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261026043614_AddTestModeAndNamedAttempts";
    private const string CurrentMigration = "20261026090737_AddKnowledgeGrading";
    private const string LatestMigration = "20261027031338_FinalizeTestGrades";
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
            SharedTokenBudget = 100_000,
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
        KnowledgeGradingLease?[] claims = await Task.WhenAll(
            new KnowledgeGradingJobQueue(queueDb1, ai, TimeProvider.System)
                .ClaimNextAsync(default),
            new KnowledgeGradingJobQueue(queueDb2, ai, TimeProvider.System)
                .ClaimNextAsync(default));
        Assert.Single(claims, value => value is not null);

        KnowledgeGradingLease firstLease = claims.Single(value => value is not null)!;
        Guid claimedJob = firstLease.JobId;
        var providerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var staleProcessorDb = new ApplicationDbContext(dbOptions);
        var staleProvider = new BlockingProvider(providerEntered, releaseProvider);
        Task staleProcessor = Processor(staleProcessorDb, staleProvider, ai)
            .ProcessAsync(firstLease, default);
        await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var expireDb = new ApplicationDbContext(dbOptions))
        {
            await expireDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE poll_knowledge_grading_jobs
                SET started_at = {DateTime.UtcNow.AddMinutes(-10)}
                WHERE id = {claimedJob}
                """);
        }
        await using var recoveryDb = new ApplicationDbContext(dbOptions);
        KnowledgeGradingLease recoveredLease = Assert.IsType<KnowledgeGradingLease>(
            await new KnowledgeGradingJobQueue(recoveryDb, ai, TimeProvider.System)
                .ClaimNextAsync(default));
        Assert.NotEqual(firstLease.LeaseId, recoveredLease.LeaseId);
        Assert.True(recoveredLease.Generation > firstLease.Generation);

        releaseProvider.SetResult();
        await staleProcessor.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var afterStale = new ApplicationDbContext(dbOptions))
        {
            Assert.Empty(afterStale.PollKnowledgeGradingSuggestions);
            Assert.Equal(KnowledgeGradingJobStatus.Processing,
                (await afterStale.PollKnowledgeGradingJobs.SingleAsync()).Status);
        }

        await using (var currentProcessorDb = new ApplicationDbContext(dbOptions))
            await Processor(currentProcessorDb, new SuccessfulProvider(), ai)
                .ProcessAsync(recoveredLease, default);
        await using (var afterCurrent = new ApplicationDbContext(dbOptions))
        {
            Assert.Single(afterCurrent.PollKnowledgeGradingSuggestions);
            Assert.Equal(KnowledgeGradingJobStatus.Completed,
                (await afterCurrent.PollKnowledgeGradingJobs.SingleAsync()).Status);
            var unchanged = await afterCurrent.NamedAttemptAnswers.SingleAsync();
            Assert.Null(unchanged.Score);
            Assert.Equal(TestAnswerGradingStates.Pending, unchanged.GradingState);
        }

        var budgetOptions = Options.Create(new AiOptions
        {
            SharedTokenBudget = 1_000,
            SharedTokenBudgetPeriodMinutes = 60
        });
        var budgetTime = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(2));
        await using var budgetDb1 = new ApplicationDbContext(dbOptions);
        await using var budgetDb2 = new ApplicationDbContext(dbOptions);
        AiBudgetReservationResult[] budgetReservations = await Task.WhenAll(
            new AiTokenBudgetManager(budgetDb1, budgetOptions, budgetTime).ReserveAsync(
                "feedback_analysis", Guid.NewGuid(), 1, 700, default),
            new AiTokenBudgetManager(budgetDb2, budgetOptions, budgetTime).ReserveAsync(
                "knowledge_grading", Guid.NewGuid(), 1, 700, default));
        Assert.Single(budgetReservations, item => item.Succeeded);
        Assert.Single(budgetReservations, item => !item.Succeeded);

        AiBudgetReservationResult accepted = budgetReservations.Single(item => item.Succeeded);
        await using (var settleDb = new ApplicationDbContext(dbOptions))
            await new AiTokenBudgetManager(settleDb, budgetOptions, budgetTime).SettleAsync(
                accepted.ReservationId!.Value, 100, 50, false, default);
        await using (var verifyBudgetDb = new ApplicationDbContext(dbOptions))
        {
            AiTokenBudgetPeriod period = await verifyBudgetDb.AiTokenBudgetPeriods
                .SingleAsync(item => item.TokenLimit == 1_000);
            Assert.Equal(0, period.ReservedTokens);
            Assert.Equal(150, period.EstimatedConsumedTokens);
            Assert.Equal(150, period.ActualTokens);
            AiTokenBudgetReservation reservation = await verifyBudgetDb
                .AiTokenBudgetReservations.SingleAsync(item => item.Id == accepted.ReservationId);
            Assert.Equal("settled", reservation.Status);
            Assert.Equal(150, reservation.ChargedTokens);
        }

        await using (var deleteDb = new ApplicationDbContext(dbOptions))
        {
            await deleteDb.Database.GetService<IMigrator>().MigrateAsync(LatestMigration);
            PollDeletionResult deleted = await new PollDeletionService(deleteDb,
                TimeProvider.System).DeleteAdminAsync(owner, poll, true, default);
            Assert.Equal(PollDeletionOutcome.Succeeded, deleted.Outcome);
        }
        await using var verify = new ApplicationDbContext(dbOptions);
        Assert.Empty(verify.PollKnowledgeGradingJobs);
        Assert.Empty(verify.PollKnowledgeGradingSuggestions);
    }

    private static KnowledgeGradingJobProcessor Processor(ApplicationDbContext db,
        IKnowledgeGradingProvider provider, IOptions<AiOptions> options) => new(
        db, provider, new AiTokenBudgetManager(db, options, TimeProvider.System), options,
        TimeProvider.System, NullLogger<KnowledgeGradingJobProcessor>.Instance);

    private sealed class SuccessfulProvider : IKnowledgeGradingProvider
    {
        public Task<KnowledgeProviderResult> GradeAsync(KnowledgeGradingInput input,
            CancellationToken cancellationToken) => Task.FromResult(new KnowledgeProviderResult(
            [new(input.Answers[0].TemporaryId, 4, input.MaxScore, "Обоснование",
                ["Верный смысл"], ["Нет примера"], false, [])], 10, 5));
    }

    private sealed class BlockingProvider(TaskCompletionSource entered,
        TaskCompletionSource release) : IKnowledgeGradingProvider
    {
        public async Task<KnowledgeProviderResult> GradeAsync(KnowledgeGradingInput input,
            CancellationToken cancellationToken)
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new([new(input.Answers[0].TemporaryId, 1, input.MaxScore,
                "Устаревший результат", [], [], false, [])], 10, 5);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "scripts", "migrations",
                "20261026090737_add_knowledge_grading.sql");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Knowledge grading migration script not found.");
    }

    private static string Normalize(string sql) => sql.TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
