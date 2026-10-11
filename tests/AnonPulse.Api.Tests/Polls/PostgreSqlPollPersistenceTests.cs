using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class PostgreSqlPollPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_tests")
        .WithUsername("anonpulse_tests")
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
    public async Task Create_PersistsPollAndOrderedOptionsInPostgreSql()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();

        var teacher = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "postgres-teacher@example.test",
            UserName = "postgres-teacher@example.test"
        };
        dbContext.Users.Add(teacher);
        await dbContext.SaveChangesAsync();

        var service = new PollService(dbContext, TimeProvider.System);
        PollCreationResult result = await service.CreateAsync(
            teacher.Id,
            new CreatePollRequest(
                "PostgreSQL poll",
                ["First option", "Second option", "Third option"]));

        Assert.True(result.Succeeded);

        dbContext.ChangeTracker.Clear();
        Poll storedPoll = await dbContext.Polls
            .Include(poll => poll.Options)
            .SingleAsync();

        Assert.Equal(teacher.Id, storedPoll.CreatorId);
        Assert.Equal(PollStatus.Draft, storedPoll.Status);
        Assert.Equal(DateTimeKind.Utc, storedPoll.CreatedAt.Kind);
        Assert.Equal(
            [0, 1, 2],
            storedPoll.Options
                .OrderBy(option => option.Position)
                .Select(option => option.Position));
    }
}
