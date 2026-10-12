using AnonPulse.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Voting;

public sealed class VoteTimestampMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261012163600_AddPollQuestionsAndStatusTransitions";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_vote_timestamp_migration_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_PreservesExistingTimestampAndAllowsAnonymousNullTimestamp()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        Guid userId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        Guid questionId = Guid.NewGuid();
        Guid optionId = Guid.NewGuid();
        Guid existingVoteId = Guid.NewGuid();
        Guid anonymousVoteId = Guid.NewGuid();
        DateTime timestampCandidate = DateTime.UtcNow.AddDays(-1);
        DateTime existingTimestamp = timestampCandidate.AddTicks(
            -(timestampCandidate.Ticks % 10));

        await using (var context = new ApplicationDbContext(options))
        {
            IMigrator migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed",
                     "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES ({userId}, FALSE, FALSE, FALSE, FALSE, 0);

                INSERT INTO polls (id, creator_id, status, created_at)
                VALUES ({pollId}, {userId}, 'open', {existingTimestamp});

                INSERT INTO poll_questions (id, poll_id, text, position)
                VALUES ({questionId}, {pollId}, 'Question', 0);

                INSERT INTO poll_options (id, question_id, text, position)
                VALUES ({optionId}, {questionId}, 'Option', 0);

                INSERT INTO votes (id, option_id, created_at)
                VALUES ({existingVoteId}, {optionId}, {existingTimestamp});
                """);

            await migrator.MigrateAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO votes (id, option_id, created_at)
                VALUES ({anonymousVoteId}, {optionId}, NULL);
                """);
        }

        await using var verification = new ApplicationDbContext(options);
        DateTime? preservedTimestamp = await verification.Votes
            .Where(vote => vote.Id == existingVoteId)
            .Select(vote => vote.CreatedAt)
            .SingleAsync();
        DateTime? anonymousTimestamp = await verification.Votes
            .Where(vote => vote.Id == anonymousVoteId)
            .Select(vote => vote.CreatedAt)
            .SingleAsync();
        string nullable = await verification.Database
            .SqlQueryRaw<string>("""
                SELECT is_nullable AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'votes'
                  AND column_name = 'created_at'
                """)
            .SingleAsync();

        Assert.Equal(existingTimestamp, preservedTimestamp);
        Assert.Null(anonymousTimestamp);
        Assert.Equal("YES", nullable);
    }
}
