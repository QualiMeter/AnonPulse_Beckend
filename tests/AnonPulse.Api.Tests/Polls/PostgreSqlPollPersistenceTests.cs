using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            new CreatePollRequest
            {
                Title = "PostgreSQL poll",
                Questions =
                [
                    new CreatePollQuestionRequest(
                        "First question",
                        ["First option", "Second option", "Third option"]),
                    new CreatePollQuestionRequest(
                        "Second question",
                        ["Yes", "No"])
                ]
            });

        Assert.True(result.Succeeded);

        dbContext.ChangeTracker.Clear();
        Poll storedPoll = await dbContext.Polls
            .Include(poll => poll.Questions)
            .ThenInclude(question => question.Options)
            .SingleAsync();

        Assert.Equal(teacher.Id, storedPoll.CreatorId);
        Assert.Equal("PostgreSQL poll", storedPoll.Title);
        Assert.Equal(PollStatus.Draft, storedPoll.Status);
        Assert.Equal(DateTimeKind.Utc, storedPoll.CreatedAt.Kind);
        PollQuestion[] questions = storedPoll.Questions
            .OrderBy(question => question.Position)
            .ToArray();
        Assert.Equal([0, 1], questions.Select(question => question.Position));
        Assert.Equal(
            ["First question", "Second question"],
            questions.Select(question => question.Text));
        Assert.Equal(
            [0, 1, 2],
            questions[0].Options
                .OrderBy(option => option.Position)
                .Select(option => option.Position));
    }

    [Fact]
    public async Task ConcurrentPublish_OnlyOneRequestChangesDraftToOpen()
    {
        var setupOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        Guid teacherId = Guid.NewGuid();
        Guid pollId;

        await using (var setupContext = new ApplicationDbContext(setupOptions))
        {
            await setupContext.Database.MigrateAsync();
            setupContext.Users.Add(new ApplicationUser
            {
                Id = teacherId,
                Email = "concurrent-teacher@example.test",
                UserName = "concurrent-teacher@example.test"
            });
            await setupContext.SaveChangesAsync();

            var setupService = new PollService(setupContext, TimeProvider.System);
            PollCreationResult created = await setupService.CreateAsync(
                teacherId,
                new CreatePollRequest("Concurrent poll", ["Yes", "No"]));
            pollId = created.Poll!.Id;
        }

        using var barrier = new Barrier(2);
        var interceptor = new CoordinatedSaveChangesInterceptor(barrier);
        var concurrentOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        await using var firstContext = new ApplicationDbContext(concurrentOptions);
        await using var secondContext = new ApplicationDbContext(concurrentOptions);
        var firstService = new PollService(firstContext, TimeProvider.System);
        var secondService = new PollService(secondContext, TimeProvider.System);

        PollTransitionResult[] results = await Task.WhenAll(
            firstService.PublishAsync(teacherId, false, pollId),
            secondService.PublishAsync(teacherId, false, pollId));

        Assert.Single(results, result =>
            result.Outcome == PollTransitionOutcome.Succeeded);
        Assert.Single(results, result =>
            result.Outcome == PollTransitionOutcome.Conflict);

        await using var verificationContext = new ApplicationDbContext(setupOptions);
        Assert.Equal(
            PollStatus.Open,
            await verificationContext.Polls
                .Where(poll => poll.Id == pollId)
                .Select(poll => poll.Status)
                .SingleAsync());
    }

    private sealed class CoordinatedSaveChangesInterceptor(Barrier barrier)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool synchronized = await Task.Run(
                () => barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken),
                cancellationToken);

            if (!synchronized)
            {
                throw new TimeoutException("Concurrent publish test did not synchronize.");
            }

            return result;
        }
    }
}
