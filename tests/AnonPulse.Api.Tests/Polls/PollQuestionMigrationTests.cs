using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class PollQuestionMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261011212234_AddDataProtectionKeys";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_poll_migration_tests")
        .WithUsername("anonpulse_poll_migration_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync()
    {
        return _postgres.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _postgres.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task Migration_ConvertsExistingPollWithoutChangingRelatedIds()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        Guid userId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        Guid firstOptionId = Guid.NewGuid();
        Guid secondOptionId = Guid.NewGuid();
        Guid voteId = Guid.NewGuid();
        Guid participationId = Guid.NewGuid();
        Guid decisionId = Guid.NewGuid();
        DateTime utcNow = DateTime.UtcNow;

        await using (var oldContext = new ApplicationDbContext(options))
        {
            IMigrator migrator = oldContext.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users
                    ("Id", "EmailConfirmed", "PhoneNumberConfirmed",
                     "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
                VALUES
                    ({userId}, FALSE, FALSE, FALSE, FALSE, 0);

                INSERT INTO polls
                    (id, creator_id, question, status, created_at)
                VALUES
                    ({pollId}, {userId}, 'Legacy question', 'draft', {utcNow});

                INSERT INTO poll_options (id, poll_id, text, position)
                VALUES
                    ({firstOptionId}, {pollId}, 'First option', 0),
                    ({secondOptionId}, {pollId}, 'Second option', 1);

                INSERT INTO votes (id, option_id, created_at)
                VALUES ({voteId}, {firstOptionId}, {utcNow});

                INSERT INTO vote_participations (id, poll_id, token_hash, used_at)
                VALUES ({participationId}, {pollId}, {new string('a', 64)}, {utcNow});

                INSERT INTO decisions (id, poll_id, author_id, text)
                VALUES ({decisionId}, {pollId}, {userId}, 'Legacy decision');
                """);

            await migrator.MigrateAsync();
        }

        await using var migratedContext = new ApplicationDbContext(options);
        Poll poll = await migratedContext.Polls
            .Include(candidate => candidate.Questions)
            .ThenInclude(question => question.Options)
            .SingleAsync(candidate => candidate.Id == pollId);
        PollQuestion question = Assert.Single(poll.Questions);

        Assert.Equal(pollId, poll.Id);
        Assert.Null(poll.Title);
        Assert.Equal(pollId, question.Id);
        Assert.Equal(pollId, question.PollId);
        Assert.Equal("Legacy question", question.Text);
        Assert.Equal(0, question.Position);
        Assert.Equal(
            [firstOptionId, secondOptionId],
            question.Options
                .OrderBy(option => option.Position)
                .Select(option => option.Id));
        Assert.All(question.Options, option => Assert.Equal(question.Id, option.QuestionId));

        Vote vote = await migratedContext.Votes.SingleAsync();
        Assert.Equal(voteId, vote.Id);
        Assert.Equal(firstOptionId, vote.OptionId);
        VoteParticipation participation =
            await migratedContext.VoteParticipations.SingleAsync();
        Assert.Equal(participationId, participation.Id);
        Assert.Equal(pollId, participation.PollId);
        Decision decision = await migratedContext.Decisions.SingleAsync();
        Assert.Equal(decisionId, decision.Id);
        Assert.Equal(pollId, decision.PollId);
        Assert.Equal(userId, (await migratedContext.Users.SingleAsync()).Id);
    }
}
