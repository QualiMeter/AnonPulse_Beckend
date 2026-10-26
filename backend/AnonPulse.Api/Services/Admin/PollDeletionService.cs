using System.Text.Json;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Services.Admin;

public sealed class PollDeletionService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPollDeletionService
{
    public Task<PollDeletionResult> DeleteTeacherDraftAsync(
        Guid teacherId,
        Guid pollId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            teacherId,
            pollId,
            isAdmin: false,
            confirmed: false,
            cancellationToken);

    public Task<PollDeletionResult> DeleteAdminAsync(
        Guid administratorId,
        Guid pollId,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            administratorId,
            pollId,
            isAdmin: true,
            confirmed,
            cancellationToken);

    private async Task<PollDeletionResult> DeleteAsync(
        Guid actorId,
        Guid pollId,
        bool isAdmin,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        Poll? poll = await dbContext.Polls
            .FromSqlInterpolated(
                $"SELECT p.*, p.xmin FROM polls AS p WHERE p.id = {pollId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (poll is null || (!isAdmin && poll.CreatorId != actorId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PollDeletionResult(PollDeletionOutcome.NotFound);
        }

        if (!isAdmin && poll.Status != PollStatus.Draft)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(
                "only_draft_poll_can_be_deleted",
                "A teacher can delete only their own draft poll.");
        }

        if (isAdmin && poll.Status != PollStatus.Draft && !confirmed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(
                "poll_deletion_confirmation_required",
                "Deleting an open or closed poll requires confirm=true.");
        }

        int decisionCount = await dbContext.Decisions.CountAsync(
            decision => decision.PollId == pollId,
            cancellationToken);
        int participationCount = await dbContext.VoteParticipations.CountAsync(
            participation => participation.PollId == pollId,
            cancellationToken);
        int voteCount = await dbContext.Votes.CountAsync(
            vote => vote.Option.Question.PollId == pollId,
            cancellationToken);
        int optionCount = await dbContext.PollOptions.CountAsync(
            option => option.Question.PollId == pollId,
            cancellationToken);
        int questionCount = await dbContext.PollQuestions.CountAsync(
            question => question.PollId == pollId,
            cancellationToken);
        int textAnswerCount = await dbContext.PollTextAnswers.CountAsync(
            answer => answer.Question.PollId == pollId, cancellationToken);
        int identifiedSubmissionCount = await dbContext.IdentifiedPollSubmissions.CountAsync(
            submission => submission.PollId == pollId,
            cancellationToken);
        int namedAttemptCount = await dbContext.NamedAttempts.CountAsync(
            attempt => attempt.PollId == pollId, cancellationToken);

        await dbContext.Decisions
            .Where(decision => decision.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.VoteParticipations
            .Where(participation => participation.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.Votes
            .Where(vote => vote.Option.Question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.PollTextAnswers
            .Where(answer => answer.Question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.IdentifiedPollSubmissions
            .Where(submission => submission.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.NamedAttemptAnswerOptions
            .Where(selection => selection.Answer.Attempt.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.NamedAttemptAnswers
            .Where(answer => answer.Attempt.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.NamedAttempts
            .Where(attempt => attempt.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.PollQuestionCorrectOptions
            .Where(key => key.Question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.PollQuestionKnowledgeSettings
            .Where(settings => settings.Question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.PollOptions
            .Where(option => option.Question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.PollQuestions
            .Where(question => question.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.Polls
            .Where(item => item.Id == pollId)
            .ExecuteDeleteAsync(cancellationToken);
        dbContext.Entry(poll).State = EntityState.Detached;

        if (isAdmin)
        {
            dbContext.AdminAuditEvents.Add(new AdminAuditEvent
            {
                Id = Guid.NewGuid(),
                AdministratorId = actorId,
                ActionType = AdminAuditActionTypes.PollDeleted,
                TargetType = AdminAuditTargetTypes.Poll,
                TargetId = pollId,
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["result"] = "deleted",
                    ["status"] = poll.Status.ToString().ToLowerInvariant(),
                    ["questions"] = questionCount.ToString(),
                    ["options"] = optionCount.ToString(),
                    ["votes"] = voteCount.ToString(),
                    ["textAnswers"] = textAnswerCount.ToString(),
                    ["identifiedSubmissions"] = identifiedSubmissionCount.ToString(),
                    ["namedAttempts"] = namedAttemptCount.ToString(),
                    ["participations"] = participationCount.ToString(),
                    ["decisions"] = decisionCount.ToString()
                })
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PollDeletionResult(PollDeletionOutcome.Succeeded);
    }

    private static PollDeletionResult Conflict(string code, string error) =>
        new(PollDeletionOutcome.Conflict, code, error);
}
