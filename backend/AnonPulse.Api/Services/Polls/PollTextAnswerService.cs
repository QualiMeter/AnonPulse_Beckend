using System.Data;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Polls;

public sealed class PollTextAnswerService(ApplicationDbContext dbContext)
    : IPollTextAnswerService
{
    public async Task<PollTextAnswersResult> GetAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var header = await dbContext.Polls.AsNoTracking()
            .Where(poll => poll.Id == pollId)
            .Select(poll => new { poll.CreatorId, poll.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || (!isAdmin && header.CreatorId != actorId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PollTextAnswersOutcome.NotFound);
        }
        if (header.Status != PollStatus.Closed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PollTextAnswersOutcome.Conflict,
                Error: "Text answers are available only after the poll is closed.");
        }
        bool questionExists = await dbContext.PollQuestions.AsNoTracking().AnyAsync(
            question => question.Id == questionId && question.PollId == pollId &&
                        question.Type == PollQuestionType.Text, cancellationToken);
        if (!questionExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PollTextAnswersOutcome.NotFound);
        }
        IQueryable<PollTextAnswer> query = dbContext.PollTextAnswers.AsNoTracking()
            .Where(answer => answer.QuestionId == questionId);
        int count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(answer => answer.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(answer => new PollTextAnswerResponse(answer.Id, answer.Text))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PollTextAnswersOutcome.Succeeded,
            new PagedResponse<PollTextAnswerResponse>(items, page, pageSize, count,
                count == 0 ? 0 : (count + pageSize - 1) / pageSize));
    }
}
