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
    IOptions<AnonymousVoterOptions> options,
    IOptions<TextAnswerOptions> textAnswerOptions) : IPublicVotingService
{
    public PublicVotingService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        IOptions<AnonymousVoterOptions> options)
        : this(dbContext, timeProvider, options, Options.Create(new TextAnswerOptions()))
    {
    }

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

            string? respondentName = NormalizeRespondentName(request.RespondentName);
            Dictionary<string, string[]>? identityErrors =
                ValidateRespondentName(poll.ResponseMode, request.RespondentName, respondentName);
            if (identityErrors is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Invalid(identityErrors);
            }

            List<PollQuestion> questions = await dbContext.PollQuestions
                .AsNoTracking()
                .Where(question => question.PollId == pollId)
                .Include(question => question.Options)
                .ToListAsync(cancellationToken);

            Dictionary<string, string[]>? answerErrors =
                ValidateAnswers(request.Answers!, questions, textAnswerOptions.Value.MaximumLength);
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

            IdentifiedPollSubmission? submission = null;
            if (poll.ResponseMode == PollResponseMode.Identified)
            {
                submission = new IdentifiedPollSubmission
                {
                    Id = Guid.NewGuid(),
                    PollId = pollId,
                    RespondentName = respondentName!
                };
                dbContext.IdentifiedPollSubmissions.Add(submission);
            }

            foreach (VoteAnswerRequest answer in request.Answers!.Cast<VoteAnswerRequest>())
            {
                if (answer.OptionId is Guid optionId)
                {
                    dbContext.Votes.Add(new Vote
                    {
                        Id = Guid.NewGuid(),
                        OptionId = optionId,
                        CreatedAt = null,
                        IdentifiedSubmission = submission
                    });
                }
                else
                {
                    dbContext.PollTextAnswers.Add(new PollTextAnswer
                    {
                        Id = Guid.NewGuid(),
                        QuestionId = answer.QuestionId,
                        Text = answer.Text!.Trim(),
                        IdentifiedSubmission = submission
                    });
                }
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

        if (request.Answers.Count > textAnswerOptions.Value.MaximumAnswersPerRequest)
        {
            return new Dictionary<string, string[]>
            {
                [nameof(request.Answers)] = ["Too many answers were submitted."]
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
                errors[$"Answers[{index}].OptionId"] = ["OptionId must not be empty."];
            }
        }

        return errors.Count == 0 ? null : errors;
    }

    private static Dictionary<string, string[]>? ValidateAnswers(
        IReadOnlyList<VoteAnswerRequest?> answers,
        IReadOnlyCollection<PollQuestion> questions,
        int maximumTextLength)
    {
        var errors = new Dictionary<string, string[]>();
        Dictionary<Guid, PollQuestion> questionById = questions.ToDictionary(item => item.Id);
        var selectedOptionsByQuestion = new Dictionary<Guid, HashSet<Guid>>();
        var textAnswersByQuestion = new Dictionary<Guid, int>();

        for (int index = 0; index < answers.Count; index++)
        {
            VoteAnswerRequest answer = answers[index]!;
            if (!questionById.TryGetValue(answer.QuestionId, out PollQuestion? question))
            {
                errors[$"Answers[{index}].QuestionId"] =
                    ["Question does not belong to this poll."];
                continue;
            }

            if (question.Type == PollQuestionType.Text)
            {
                if (answer.OptionId is not null)
                {
                    errors[$"Answers[{index}].OptionId"] =
                        ["OptionId is not allowed for a text question."];
                }
                string text = answer.Text?.Trim() ?? string.Empty;
                if (text.Length == 0 || text.Length > maximumTextLength)
                {
                    errors[$"Answers[{index}].Text"] =
                        [$"Text must contain 1 to {maximumTextLength} characters."];
                }
                textAnswersByQuestion[question.Id] =
                    textAnswersByQuestion.GetValueOrDefault(question.Id) + 1;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(answer.Text))
            {
                errors[$"Answers[{index}].Text"] =
                    ["Text is not allowed for a choice question."];
            }
            if (answer.OptionId is null ||
                question.Options.All(option => option.Id != answer.OptionId.Value))
            {
                errors[$"Answers[{index}].OptionId"] =
                    ["Option does not belong to the specified question."];
                continue;
            }

            if (!selectedOptionsByQuestion.TryGetValue(
                    question.Id,
                    out HashSet<Guid>? selectedOptions))
            {
                selectedOptions = [];
                selectedOptionsByQuestion.Add(question.Id, selectedOptions);
            }

            if (!selectedOptions.Add(answer.OptionId.Value))
            {
                errors[$"Answers[{index}].OptionId"] =
                    ["The same option may not be selected more than once."];
            }
        }

        foreach (PollQuestion question in questions)
        {
            if (question.Type == PollQuestionType.Text)
            {
                if (textAnswersByQuestion.GetValueOrDefault(question.Id) != 1)
                {
                    errors[$"Questions[{question.Id}]"] =
                        ["Exactly one text answer is required for this question."];
                }
                continue;
            }
            int selectedCount = selectedOptionsByQuestion
                .GetValueOrDefault(question.Id)?.Count ?? 0;
            if (selectedCount == 0)
            {
                errors[$"Questions[{question.Id}]"] = ["Question must be answered."];
            }
            else if (question.Type == PollQuestionType.SingleChoice && selectedCount != 1)
            {
                errors[$"Questions[{question.Id}]"] =
                    ["Exactly one option must be selected for this question."];
            }
        }

        return errors.Count == 0 ? null : errors;
    }

    private static Dictionary<string, string[]>? ValidateRespondentName(
        PollResponseMode responseMode,
        string? suppliedName,
        string? normalizedName)
    {
        if (responseMode == PollResponseMode.Anonymous)
        {
            return suppliedName is null
                ? null
                : new Dictionary<string, string[]>
                {
                    ["RespondentName"] =
                        ["RespondentName is not allowed for an anonymous poll."]
                };
        }

        if (normalizedName is null || normalizedName.Length > 150)
        {
            return new Dictionary<string, string[]>
            {
                ["RespondentName"] =
                    ["RespondentName must contain 1 to 150 characters."]
            };
        }

        return null;
    }

    private static string? NormalizeRespondentName(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
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
                        .ToArray(),
                    question.Type == PollQuestionType.MultipleChoice,
                    question.Type.ToApiValue(),
                    question.TextPurpose.ToApiValue()))
                .ToArray(),
            poll.ResponseMode.ToApiValue());
}
