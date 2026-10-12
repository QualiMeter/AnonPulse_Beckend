using AnonPulse.Api.Contracts.PublicPolls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AnonPulse.Api.Services.Voting;

public sealed class PublicVotingService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AnonymousVoterOptions> options) : IPublicVotingService
{
    private const string ParticipationUniqueConstraint =
        "ux_vote_participations_poll_id_token_hash";

    public async Task<PublicPollResult> GetOpenPollAsync(
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        Poll? poll = await dbContext.Polls
            .AsNoTracking()
            .Include(item => item.Questions)
            .ThenInclude(question => question.Options)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == pollId, cancellationToken);

        if (poll is null)
        {
            return new PublicPollResult(PublicPollOutcome.NotFound);
        }

        return poll.Status == PollStatus.Open
            ? new PublicPollResult(PublicPollOutcome.Succeeded, ToResponse(poll))
            : new PublicPollResult(PublicPollOutcome.NotOpen);
    }

    public async Task<VoteSubmissionResult> SubmitAsync(
        Guid pollId,
        SubmitVotesRequest request,
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string[]>? requestErrors = ValidateShape(request);
        if (requestErrors is not null)
        {
            return Invalid(requestErrors);
        }

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            Poll? poll = await LockPollAsync(pollId, cancellationToken);
            if (poll is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new VoteSubmissionResult(VoteSubmissionOutcome.NotFound);
            }

            if (poll.Status != PollStatus.Open)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict("Poll is not open for voting.");
            }

            List<PollQuestion> questions = await dbContext.PollQuestions
                .AsNoTracking()
                .Where(question => question.PollId == pollId)
                .Include(question => question.Options)
                .ToListAsync(cancellationToken);

            Dictionary<string, string[]>? answerErrors =
                ValidateAnswers(request.Answers!, questions);
            if (answerErrors is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Invalid(answerErrors);
            }

            DateTime storedTime = Coarsen(timeProvider.GetUtcNow().UtcDateTime);
            dbContext.VoteParticipations.Add(new VoteParticipation
            {
                Id = Guid.NewGuid(),
                PollId = pollId,
                TokenHash = tokenHash,
                UsedAt = storedTime
            });

            foreach (VoteAnswerRequest answer in request.Answers!.Cast<VoteAnswerRequest>())
            {
                dbContext.Votes.Add(new Vote
                {
                    Id = Guid.NewGuid(),
                    OptionId = answer.OptionId,
                    CreatedAt = storedTime
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VoteSubmissionResult(VoteSubmissionOutcome.Succeeded);
        }
        catch (DbUpdateException exception) when (IsDuplicateParticipation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return Conflict("This browser has already participated in this poll.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Poll?> LockPollAsync(
        Guid pollId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Polls
            .FromSqlInterpolated(
                $"SELECT p.*, p.xmin FROM polls AS p WHERE p.id = {pollId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private Dictionary<string, string[]>? ValidateShape(SubmitVotesRequest request)
    {
        if (request.Answers is null || request.Answers.Count == 0)
        {
            return new Dictionary<string, string[]>
            {
                [nameof(request.Answers)] = ["At least one answer is required."]
            };
        }

        var errors = new Dictionary<string, string[]>();
        for (int index = 0; index < request.Answers.Count; index++)
        {
            VoteAnswerRequest? answer = request.Answers[index];
            if (answer is null)
            {
                errors[$"Answers[{index}]"] = ["Answer must not be null."];
                continue;
            }

            if (answer.QuestionId == Guid.Empty)
            {
                errors[$"Answers[{index}].QuestionId"] = ["QuestionId is required."];
            }

            if (answer.OptionId == Guid.Empty)
            {
                errors[$"Answers[{index}].OptionId"] = ["OptionId is required."];
            }
        }

        return errors.Count == 0 ? null : errors;
    }

    private static Dictionary<string, string[]>? ValidateAnswers(
        IReadOnlyList<VoteAnswerRequest?> answers,
        IReadOnlyCollection<PollQuestion> questions)
    {
        var errors = new Dictionary<string, string[]>();
        Dictionary<Guid, PollQuestion> questionById = questions.ToDictionary(item => item.Id);
        HashSet<Guid> answeredQuestions = [];

        if (answers.Count != questions.Count)
        {
            errors[nameof(SubmitVotesRequest.Answers)] =
                ["Exactly one answer is required for every poll question."];
        }

        for (int index = 0; index < answers.Count; index++)
        {
            VoteAnswerRequest answer = answers[index]!;
            if (!answeredQuestions.Add(answer.QuestionId))
            {
                errors[$"Answers[{index}].QuestionId"] =
                    ["Each question may be answered only once."];
                continue;
            }

            if (!questionById.TryGetValue(answer.QuestionId, out PollQuestion? question))
            {
                errors[$"Answers[{index}].QuestionId"] =
                    ["Question does not belong to this poll."];
                continue;
            }

            if (question.Options.All(option => option.Id != answer.OptionId))
            {
                errors[$"Answers[{index}].OptionId"] =
                    ["Option does not belong to the specified question."];
            }
        }

        foreach (PollQuestion question in questions)
        {
            if (!answeredQuestions.Contains(question.Id))
            {
                errors[$"Questions[{question.Id}]"] = ["Question must be answered."];
            }
        }

        return errors.Count == 0 ? null : errors;
    }

    private DateTime Coarsen(DateTime utcNow)
    {
        long precisionTicks = options.Value.TimestampPrecision.Ticks;
        if (precisionTicks <= 0)
        {
            throw new InvalidOperationException(
                "AnonymousVoting:TimestampPrecision must be greater than zero.");
        }

        return new DateTime(
            utcNow.Ticks - utcNow.Ticks % precisionTicks,
            DateTimeKind.Utc);
    }

    private static bool IsDuplicateParticipation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException &&
               postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(
                   postgresException.ConstraintName,
                   ParticipationUniqueConstraint,
                   StringComparison.Ordinal);
    }

    private static VoteSubmissionResult Invalid(
        IReadOnlyDictionary<string, string[]> errors) =>
        new(VoteSubmissionOutcome.Invalid, errors);

    private static VoteSubmissionResult Conflict(string error) =>
        new(VoteSubmissionOutcome.Conflict, Error: error);

    private static PublicPollResponse ToResponse(Poll poll) =>
        new(
            poll.Id,
            poll.Title,
            poll.Questions
                .OrderBy(question => question.Position)
                .Select(question => new PublicPollQuestionResponse(
                    question.Id,
                    question.Text,
                    question.Position,
                    question.Options
                        .OrderBy(option => option.Position)
                        .Select(option => new PublicPollOptionResponse(
                            option.Id,
                            option.Text,
                            option.Position))
                        .ToArray()))
                .ToArray());
}
