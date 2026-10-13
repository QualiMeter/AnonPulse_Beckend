using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.PollResults;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonPulse.Api.Tests.PollResults;

public sealed class PollResultsServiceTests
{
    [Fact]
    public async Task GetAsync_CalculatesRoundedPercentagesAndParticipants()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        var teacher = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "results-unit@example.test",
            UserName = "results-unit@example.test"
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
            Text = "Question",
            Position = 0
        };
        var firstOption = new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Text = "First",
            Position = 0
        };
        var secondOption = new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Text = "Second",
            Position = 1
        };

        question.Options.Add(firstOption);
        question.Options.Add(secondOption);
        poll.Questions.Add(question);
        firstOption.Votes.Add(NewVote(firstOption.Id));
        for (int index = 0; index < 5; index++)
        {
            secondOption.Votes.Add(NewVote(secondOption.Id));
            poll.VoteParticipations.Add(NewParticipation(poll.Id, index));
        }

        poll.VoteParticipations.Add(NewParticipation(poll.Id, 5));
        db.Users.Add(teacher);
        db.Polls.Add(poll);
        await db.SaveChangesAsync();

        var service = new PollResultsService(db);
        PollResultsResult result = await service.GetAsync(
            poll.Id,
            new PollResultsAccessContext(
                teacher.Id,
                IsAuthenticated: true,
                IsTeacher: true,
                IsAdmin: false));

        Assert.Equal(PollResultsOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Results);
        Assert.Equal("open", result.Results.Status);
        Assert.Equal("interim", result.Results.ResultsType);
        Assert.Equal(6, result.Results.TotalParticipants);
        Assert.Equal([1, 5], result.Results.Questions[0].Options.Select(x => x.VoteCount));
        Assert.Equal(
            [16.67m, 83.33m],
            result.Results.Questions[0].Options.Select(x => x.Percentage));
    }

    private static Vote NewVote(Guid optionId) => new()
    {
        Id = Guid.NewGuid(),
        OptionId = optionId,
        CreatedAt = null
    };

    private static VoteParticipation NewParticipation(Guid pollId, int index) => new()
    {
        Id = Guid.NewGuid(),
        PollId = pollId,
        TokenHash = index.ToString("x64"),
        UsedAt = DateTime.UtcNow
    };
}
