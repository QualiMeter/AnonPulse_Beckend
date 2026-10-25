using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task Migration_BackfillsBothLegacyTypesAndPreservesVotes()
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
        await using var verify = new ApplicationDbContext(options);
        PollQuestion[] questions = await verify.PollQuestions.OrderBy(q => q.Position).ToArrayAsync();
        Assert.Equal(PollQuestionType.SingleChoice, questions[0].Type);
        Assert.Equal(PollQuestionType.MultipleChoice, questions[1].Type);
        Assert.True(await verify.Votes.AnyAsync(v => v.Id == vote));
        Assert.Empty(verify.PollTextAnswers);
        Assert.Empty(verify.PollQuestionKnowledgeSettings);
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
        Assert.Equal(4000, textMaximumLength);
        Assert.Contains("100", maxScoreConstraint, StringComparison.Ordinal);
    }
}
