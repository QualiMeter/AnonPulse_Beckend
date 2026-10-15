using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class MultipleAnswerMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261012185504_MakeVoteCreatedAtNullable";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_multiple_answer_migration_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_PreservesExistingPollVotesAndDefaultsQuestionToSingleChoice()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        Guid userId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        Guid questionId = Guid.NewGuid();
        Guid optionId = Guid.NewGuid();
        Guid voteId = Guid.NewGuid();
        Guid participationId = Guid.NewGuid();
        DateTime utcNow = DateTime.UtcNow;

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
                VALUES ({pollId}, {userId}, 'closed', {utcNow});

                INSERT INTO poll_questions (id, poll_id, text, position)
                VALUES ({questionId}, {pollId}, 'Existing question', 0);

                INSERT INTO poll_options (id, question_id, text, position)
                VALUES ({optionId}, {questionId}, 'Existing option', 0);

                INSERT INTO votes (id, option_id, created_at)
                VALUES ({voteId}, {optionId}, NULL);

                INSERT INTO vote_participations (id, poll_id, token_hash, used_at)
                VALUES ({participationId}, {pollId}, {new string('b', 64)}, {utcNow});
                """);

            await migrator.MigrateAsync();
        }

        await using var verification = new ApplicationDbContext(options);
        PollQuestion question = await verification.PollQuestions
            .SingleAsync(item => item.Id == questionId);
        Vote vote = await verification.Votes.SingleAsync(item => item.Id == voteId);
        VoteParticipation participation = await verification.VoteParticipations
            .SingleAsync(item => item.Id == participationId);
        string defaultExpression = await verification.Database
            .SqlQueryRaw<string>("""
                SELECT column_default AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'poll_questions'
                  AND column_name = 'allows_multiple_answers'
                """)
            .SingleAsync();

        Assert.False(question.AllowsMultipleAnswers);
        Assert.Equal(optionId, vote.OptionId);
        Assert.Equal(pollId, participation.PollId);
        Assert.Contains("false", defaultExpression, StringComparison.OrdinalIgnoreCase);
    }
}
