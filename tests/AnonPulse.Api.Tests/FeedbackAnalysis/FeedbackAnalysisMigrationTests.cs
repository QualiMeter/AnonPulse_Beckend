using AnonPulse.Api.Data;
using AnonPulse.Api.Contracts.FeedbackAnalysis;
using AnonPulse.Api.Services.Admin;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.FeedbackAnalysis;

public sealed class FeedbackAnalysisMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261025052103_AddIdentifiedPolls";
    private const string CurrentMigration = "20261025072432_AddFeedbackAnalyses";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("anonpulse_feedback_analysis_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task MigrationScriptMatchesModel_ConcurrentStartIsUnique_AndPollDeleteCascades()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var migrate = new ApplicationDbContext(options))
        {
            IMigrator migrator = migrate.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            string generated = migrator.GenerateScript(PreviousMigration, CurrentMigration,
                MigrationsSqlGenerationOptions.Idempotent);
            Assert.Equal(Normalize(generated), Normalize(await File.ReadAllTextAsync(FindScript())));
            await migrate.Database.ExecuteSqlRawAsync(generated);
        }

        Guid owner = Guid.NewGuid(), poll = Guid.NewGuid(), question = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
                     "LockoutEnabled", "AccessFailedCount", is_administratively_deactivated)
                VALUES ({owner}, FALSE, FALSE, FALSE, FALSE, 0, FALSE);
                INSERT INTO polls (id, creator_id, status, response_mode, created_at)
                VALUES ({poll}, {owner}, 'closed', 'anonymous', {DateTime.UtcNow});
                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers, question_type, text_purpose)
                VALUES ({question}, {poll}, 'Feedback', 0, FALSE, 'text', 'feedback');
                INSERT INTO poll_text_answers (id, question_id, text)
                VALUES ({Guid.NewGuid()}, {question}, 'Больше практики');
                """);
        }

        var settings = Options.Create(new AiOptions
        {
            Enabled = true,
            Groq = new GroqOptions { ApiKey = "fake-test-key" }
        });
        await using var firstDb = new ApplicationDbContext(options);
        await using var secondDb = new ApplicationDbContext(options);
        var firstService = new FeedbackAnalysisService(firstDb, settings, TimeProvider.System);
        var secondService = new FeedbackAnalysisService(secondDb, settings, TimeProvider.System);
        FeedbackAnalysisServiceResult[] results = await Task.WhenAll(
            firstService.StartAsync(owner, false, poll, question, true, default),
            secondService.StartAsync(owner, false, poll, question, true, default));

        Guid analysisId = Assert.IsType<FeedbackAnalysisResponse>(results[0].Analysis).Id;
        Assert.Equal(analysisId, results[1].Analysis?.Id);
        await using var firstQueueDb = new ApplicationDbContext(options);
        await using var secondQueueDb = new ApplicationDbContext(options);
        var firstQueue = new FeedbackAnalysisJobQueue(firstQueueDb, settings, TimeProvider.System);
        var secondQueue = new FeedbackAnalysisJobQueue(secondQueueDb, settings, TimeProvider.System);
        Guid?[] claims = await Task.WhenAll(firstQueue.ClaimNextAsync(default),
            secondQueue.ClaimNextAsync(default));
        Assert.Single(claims, item => item is not null);
        Assert.Equal(analysisId, claims.Single(item => item is not null));

        await using (var verify = new ApplicationDbContext(options))
        {
            Assert.Equal(1, await verify.PollFeedbackAnalyses.CountAsync());
            await verify.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE poll_feedback_analyses
                SET started_at = {DateTime.UtcNow.AddMinutes(-10)}
                WHERE id = {analysisId}
                """);
            var recoveryQueue = new FeedbackAnalysisJobQueue(verify, settings, TimeProvider.System);
            Assert.Equal(analysisId, await recoveryQueue.ClaimNextAsync(default));
            Assert.Equal(2, await verify.PollFeedbackAnalyses
                .Where(item => item.Id == analysisId)
                .Select(item => item.Attempts).SingleAsync());
            var deletion = new PollDeletionService(verify, TimeProvider.System);
            PollDeletionResult result = await deletion.DeleteAdminAsync(owner, poll, true, default);
            Assert.Equal(PollDeletionOutcome.Succeeded, result.Outcome);
        }
        await using var afterDelete = new ApplicationDbContext(options);
        Assert.Empty(afterDelete.PollFeedbackAnalyses);
    }

    private static string FindScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "scripts", "migrations",
                "20261025072432_add_feedback_analyses.sql");
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Feedback analysis migration script was not found.");
    }

    private static string Normalize(string sql) => sql.TrimStart('\uFEFF')
        .Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
