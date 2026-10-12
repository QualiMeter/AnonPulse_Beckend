using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using AnonPulse.Api.Services.Voting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace AnonPulse.Api.Tests.Voting;

public sealed class VotingConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("anonpulse_concurrency_tests")
        .WithUsername("anonpulse_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();
    private DbContextOptions<ApplicationDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var db = new ApplicationDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task SaveFailure_RollsBackParticipationAndAllVotes()
    {
        SeededPoll poll = await SeedOpenPollAsync();
        var failingOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .AddInterceptors(new ThrowAfterSaveInterceptor())
            .Options;
        await using var failingDb = new ApplicationDbContext(failingOptions);
        var service = CreateVotingService(failingDb);

        await Assert.ThrowsAsync<SimulatedSaveException>(() => service.SubmitAsync(
            poll.Id,
            ValidRequest(poll),
            new string('a', 64)));

        await using var verificationDb = new ApplicationDbContext(_options);
        Assert.Empty(await verificationDb.VoteParticipations
            .Where(item => item.PollId == poll.Id)
            .ToListAsync());
        Assert.Empty(await verificationDb.Votes
            .Where(vote => vote.Option.Question.PollId == poll.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task CloseAndVoteRace_AreSerializedOnPollRow()
    {
        for (int iteration = 0; iteration < 5; iteration++)
        {
            SeededPoll poll = await SeedOpenPollAsync();
            await using var votingDb = new ApplicationDbContext(_options);
            await using var closingDb = new ApplicationDbContext(_options);
            var votingService = CreateVotingService(votingDb);
            var pollService = new PollService(closingDb, TimeProvider.System);

            Task<VoteSubmissionResult> voteTask = votingService.SubmitAsync(
                poll.Id,
                ValidRequest(poll),
                iteration.ToString("x64"));
            Task<PollTransitionResult> closeTask = pollService.CloseAsync(
                poll.TeacherId,
                false,
                poll.Id);

            await Task.WhenAll(voteTask, closeTask);
            VoteSubmissionResult voteResult = await voteTask;
            PollTransitionResult closeResult = await closeTask;

            Assert.Equal(PollTransitionOutcome.Succeeded, closeResult.Outcome);
            Assert.Contains(
                voteResult.Outcome,
                new[] { VoteSubmissionOutcome.Succeeded, VoteSubmissionOutcome.Conflict });

            await using var verificationDb = new ApplicationDbContext(_options);
            Assert.Equal(
                PollStatus.Closed,
                await verificationDb.Polls
                    .Where(item => item.Id == poll.Id)
                    .Select(item => item.Status)
                    .SingleAsync());
            int voteCount = await verificationDb.Votes
                .CountAsync(vote => vote.Option.Question.PollId == poll.Id);
            Assert.Equal(
                voteResult.Outcome == VoteSubmissionOutcome.Succeeded ? 1 : 0,
                voteCount);
        }
    }

    private PublicVotingService CreateVotingService(ApplicationDbContext db) =>
        new(
            db,
            TimeProvider.System,
            Options.Create(new AnonymousVoterOptions()));

    private async Task<SeededPoll> SeedOpenPollAsync()
    {
        await using var db = new ApplicationDbContext(_options);
        var teacher = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"teacher-{Guid.NewGuid()}@example.test",
            Email = $"teacher-{Guid.NewGuid()}@example.test"
        };
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = teacher.Id,
            Status = PollStatus.Open,
            CreatedAt = DateTime.UtcNow,
            PublishedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Position = 0,
            Text = "Question"
        };
        var option = new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Position = 0,
            Text = "First"
        };
        question.Options.Add(option);
        question.Options.Add(new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Position = 1,
            Text = "Second"
        });
        poll.Questions.Add(question);
        db.Users.Add(teacher);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return new SeededPoll(poll.Id, teacher.Id, question.Id, option.Id);
    }

    private static SubmitVotesRequest ValidRequest(SeededPoll poll) =>
        new([new VoteAnswerRequest(poll.QuestionId, poll.OptionId)]);

    private sealed record SeededPoll(
        Guid Id,
        Guid TeacherId,
        Guid QuestionId,
        Guid OptionId);

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new SimulatedSaveException());
    }

    private sealed class SimulatedSaveException : Exception;
}
