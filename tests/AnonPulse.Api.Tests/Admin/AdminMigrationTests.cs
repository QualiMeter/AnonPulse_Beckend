using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Admin;

public sealed class AdminMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261015065316_AddMultipleAnswerQuestions";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_admin_migration_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_PreservesExistingDataAndDefaultsUsersToActive()
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
                VALUES ({pollId}, {userId}, 'closed', {DateTime.UtcNow});

                INSERT INTO poll_questions
                    (id, poll_id, text, position, allows_multiple_answers)
                VALUES ({questionId}, {pollId}, 'Existing question', 0, FALSE);

                INSERT INTO poll_options (id, question_id, text, position)
                VALUES ({optionId}, {questionId}, 'Existing option', 0);

                INSERT INTO votes (id, option_id, created_at)
                VALUES ({voteId}, {optionId}, NULL);

                INSERT INTO vote_participations (id, poll_id, token_hash, used_at)
                VALUES ({participationId}, {pollId}, {new string('c', 64)}, {DateTime.UtcNow});
                """);
            await migrator.MigrateAsync();
        }

        await using var verification = new ApplicationDbContext(options);
        ApplicationUser user = await verification.Users.SingleAsync(item => item.Id == userId);
        Assert.False(user.IsAdministrativelyDeactivated);
        Assert.True(await verification.Polls.AnyAsync(item => item.Id == pollId));
        Assert.True(await verification.PollQuestions.AnyAsync(item => item.Id == questionId));
        Assert.True(await verification.PollOptions.AnyAsync(item => item.Id == optionId));
        Assert.True(await verification.Votes.AnyAsync(item => item.Id == voteId));
        Assert.True(await verification.VoteParticipations.AnyAsync(
            item => item.Id == participationId));
        Assert.True(await verification.Database.CanConnectAsync());
        Assert.Empty(verification.AdminAuditEvents);
    }
}
