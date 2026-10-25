using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonPulse.Api.Tests.Polls;

public sealed class PollServiceResponseModeTests
{
    [Fact]
    public async Task NonRelationalUpdateWithoutResponseMode_PreservesCurrentMode()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new ApplicationDbContext(options);
        Guid creatorId = Guid.NewGuid();
        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Status = PollStatus.Draft,
            ResponseMode = PollResponseMode.Identified,
            CreatedAt = DateTime.UtcNow
        };
        var question = new PollQuestion
        {
            Id = Guid.NewGuid(),
            PollId = poll.Id,
            Text = "Original question",
            Position = 0
        };
        question.Options.Add(new PollOption
        {
            Id = Guid.NewGuid(),
            QuestionId = question.Id,
            Text = "Original option",
            Position = 0
        });
        poll.Questions.Add(question);
        dbContext.Polls.Add(poll);
        await dbContext.SaveChangesAsync();

        var service = new PollService(dbContext, TimeProvider.System);
        PollUpdateResult result = await service.UpdateAsync(
            creatorId,
            poll.Id,
            new CreatePollRequest("Updated question", ["Yes", "No"]));

        Assert.Equal(PollUpdateOutcome.Succeeded, result.Outcome);
        Assert.Equal("identified", result.Poll?.ResponseMode);
        Assert.Equal(
            PollResponseMode.Identified,
            (await dbContext.Polls.SingleAsync(item => item.Id == poll.Id)).ResponseMode);
    }
}
