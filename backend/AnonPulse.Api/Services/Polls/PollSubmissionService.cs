using System.Data;
using AnonPulse.Api.Contracts.Admin;
using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AnonPulse.Api.Services.Polls;

public sealed class PollSubmissionService(ApplicationDbContext dbContext)
    : IPollSubmissionService
{
    public async Task<PollSubmissionsResult> GetAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var header = await dbContext.Polls
            .AsNoTracking()
            .Where(poll => poll.Id == pollId)
            .Select(poll => new { poll.CreatorId, poll.Status, poll.ResponseMode })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || (!isAdmin && header.CreatorId != actorId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PollSubmissionsResult(PollSubmissionsOutcome.NotFound);
        }

        if (header.ResponseMode != PollResponseMode.Identified)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Individual submissions are not available for anonymous polls.");
        }

        if (header.Status != PollStatus.Closed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Identified submissions are available only after the poll is closed.");
        }

        IQueryable<IdentifiedPollSubmission> submissions =
            dbContext.IdentifiedPollSubmissions
                .AsNoTracking()
                .Where(submission => submission.PollId == pollId);
        int totalCount = await submissions.CountAsync(cancellationToken);
        long offset = ((long)page - 1L) * pageSize;
        IdentifiedPollSubmission[] pageItems = offset >= totalCount
            ? []
            : await submissions
                .OrderBy(submission => submission.Id)
                .Skip((int)offset)
                .Take(pageSize)
                .ToArrayAsync(cancellationToken);

        Guid[] submissionIds = pageItems.Select(item => item.Id).ToArray();
        QuestionRow[] questions = await dbContext.PollQuestions
            .AsNoTracking()
            .Where(question => question.PollId == pollId)
            .OrderBy(question => question.Position)
            .ThenBy(question => question.Id)
            .Select(question => new QuestionRow(question.Id, question.Type))
            .ToArrayAsync(cancellationToken);
        VoteRow[] votes = submissionIds.Length == 0
            ? []
            : await dbContext.Votes
                .AsNoTracking()
                .Where(vote => vote.IdentifiedSubmissionId != null &&
                               submissionIds.Contains(vote.IdentifiedSubmissionId.Value))
                .Select(vote => new VoteRow(
                    vote.IdentifiedSubmissionId!.Value,
                    vote.Option.QuestionId,
                    vote.OptionId))
                .ToArrayAsync(cancellationToken);
        TextRow[] texts = submissionIds.Length == 0
            ? []
            : await dbContext.PollTextAnswers
                .AsNoTracking()
                .Where(answer => answer.IdentifiedSubmissionId != null &&
                                 submissionIds.Contains(answer.IdentifiedSubmissionId.Value))
                .Select(answer => new TextRow(
                    answer.IdentifiedSubmissionId!.Value,
                    answer.QuestionId,
                    answer.Text))
                .ToArrayAsync(cancellationToken);

        IdentifiedPollSubmissionResponse[] items = pageItems
            .Select(submission => new IdentifiedPollSubmissionResponse(
                submission.Id,
                submission.RespondentName,
                questions.Select(question => new IdentifiedPollAnswerResponse(
                    question.Id,
                    votes.Where(vote => vote.SubmissionId == submission.Id &&
                                        vote.QuestionId == question.Id)
                        .Select(vote => vote.OptionId)
                        .Order()
                        .ToArray(),
                    question.Type == PollQuestionType.Text
                        ? texts.SingleOrDefault(text =>
                            text.SubmissionId == submission.Id &&
                            text.QuestionId == question.Id)?.Text
                        : null)).ToArray()))
            .ToArray();

        await transaction.CommitAsync(cancellationToken);
        return new PollSubmissionsResult(
            PollSubmissionsOutcome.Succeeded,
            new PagedResponse<IdentifiedPollSubmissionResponse>(
                items,
                page,
                pageSize,
                totalCount,
                totalCount == 0 ? 0 : ((totalCount - 1) / pageSize) + 1));
    }

    private static PollSubmissionsResult Conflict(string error) =>
        new(PollSubmissionsOutcome.Conflict, Error: error);

    private sealed record QuestionRow(Guid Id, PollQuestionType Type);
    private sealed record VoteRow(Guid SubmissionId, Guid QuestionId, Guid OptionId);
    private sealed record TextRow(Guid SubmissionId, Guid QuestionId, string Text);
}
