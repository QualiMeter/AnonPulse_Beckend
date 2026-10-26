using System.Data;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Tests;

public sealed class TestAttemptService(ApplicationDbContext dbContext)
    : ITestAttemptService
{
    public async Task<TestAttemptsResult> GetAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var header = await dbContext.Polls.AsNoTracking()
            .Where(poll => poll.Id == pollId)
            .Select(poll => new { poll.CreatorId, poll.Status, poll.Kind })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || (!isAdmin && header.CreatorId != actorId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TestAttemptsResult(TestAttemptsOutcome.NotFound);
        }
        if (header.Kind != PollKind.Test)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Named test attempts are available only for tests.");
        }
        if (header.Status != PollStatus.Closed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Test attempts are available only after the test is closed.");
        }

        IQueryable<NamedAttempt> query = dbContext.NamedAttempts.AsNoTracking()
            .Where(attempt => attempt.PollId == pollId);
        int totalCount = await query.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        NamedAttempt[] attempts = offset >= totalCount ? [] : await query
            .Include(attempt => attempt.Answers)
            .ThenInclude(answer => answer.SelectedOptions)
            .OrderBy(attempt => attempt.CreatedAt)
            .ThenBy(attempt => attempt.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .AsSplitQuery()
            .ToArrayAsync(cancellationToken);
        TestAttemptResponse[] items = attempts.Select(attempt => new TestAttemptResponse(
            attempt.Id,
            attempt.RespondentName,
            attempt.RespondentGroup,
            attempt.State,
            attempt.CreatedAt,
            attempt.CompletedAt,
            attempt.RawScore,
            attempt.MaximumScore,
            attempt.NormalizedScore,
            attempt.IsFullyGraded,
            attempt.IsFinal,
            attempt.Answers.OrderBy(answer => answer.QuestionId)
                .Select(answer => new TestAttemptAnswerResponse(
                    answer.QuestionId,
                    answer.SelectedOptions.Select(item => item.OptionId).Order().ToArray(),
                    answer.Text,
                    answer.GradingState,
                    answer.Score,
                    answer.MaximumScore))
                .ToArray()))
            .ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new TestAttemptsResult(
            TestAttemptsOutcome.Succeeded,
            new PagedResponse<TestAttemptResponse>(
                items, page, pageSize, totalCount,
                totalCount == 0 ? 0 : ((totalCount - 1) / pageSize) + 1));
    }

    private static TestAttemptsResult Conflict(string error) =>
        new(TestAttemptsOutcome.Conflict, Error: error);
}
