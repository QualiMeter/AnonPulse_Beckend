using AnonPulse.Api.Contracts.Decisions;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Decisions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnonPulse.Api.Tests.Decisions;

public sealed class DecisionServiceTests
{
    [Fact]
    public async Task UnchangedText_PreservesTimestampAndOriginalAuthor()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        Guid ownerId = Guid.NewGuid();
        Guid adminId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        DateTime publishedAt = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
        db.Users.AddRange(
            NewUser(ownerId, "owner@example.test"),
            NewUser(adminId, "admin@example.test"));
        db.Polls.Add(new Poll
        {
            Id = pollId,
            CreatorId = ownerId,
            Status = PollStatus.Closed,
            CreatedAt = publishedAt.AddDays(-1),
            PublishedAt = publishedAt.AddHours(-1),
            ClosedAt = publishedAt,
            Decision = new Decision
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                AuthorId = ownerId,
                Text = "Stable text",
                PublishedAt = publishedAt
            }
        });
        await db.SaveChangesAsync();
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(publishedAt.AddDays(1)));
        var service = new DecisionService(db, timeProvider);

        DecisionUpsertResult result = await service.UpsertAsync(
            adminId,
            isAdmin: true,
            pollId,
            new UpsertDecisionRequest("  Stable text  "));

        Assert.Equal(DecisionUpsertOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Decision);
        Assert.Equal(publishedAt, result.Decision.PublishedAt);
        Decision stored = await db.Decisions.SingleAsync();
        Assert.Equal(ownerId, stored.AuthorId);
        Assert.Equal(EntityState.Unchanged, db.Entry(stored).State);
    }

    [Fact]
    public async Task ChangedText_UpdatesTimestampButPreservesOriginalAuthor()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ApplicationDbContext(options);
        Guid ownerId = Guid.NewGuid();
        Guid adminId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        DateTime originalTime = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
        DateTime updatedTime = originalTime.AddHours(2);
        db.Users.AddRange(
            NewUser(ownerId, "owner-change@example.test"),
            NewUser(adminId, "admin-change@example.test"));
        db.Polls.Add(new Poll
        {
            Id = pollId,
            CreatorId = ownerId,
            Status = PollStatus.Closed,
            CreatedAt = originalTime.AddDays(-1),
            PublishedAt = originalTime.AddHours(-1),
            ClosedAt = originalTime,
            Decision = new Decision
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                AuthorId = ownerId,
                Text = "Original",
                PublishedAt = originalTime
            }
        });
        await db.SaveChangesAsync();
        var service = new DecisionService(
            db,
            new ManualTimeProvider(new DateTimeOffset(updatedTime)));

        DecisionUpsertResult result = await service.UpsertAsync(
            adminId,
            isAdmin: true,
            pollId,
            new UpsertDecisionRequest("Updated"));

        Assert.Equal(DecisionUpsertOutcome.Succeeded, result.Outcome);
        Decision stored = await db.Decisions.SingleAsync();
        Assert.Equal("Updated", stored.Text);
        Assert.Equal(updatedTime, stored.PublishedAt);
        Assert.Equal(ownerId, stored.AuthorId);
    }

    private static ApplicationUser NewUser(Guid id, string email) => new()
    {
        Id = id,
        Email = email,
        UserName = email
    };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
