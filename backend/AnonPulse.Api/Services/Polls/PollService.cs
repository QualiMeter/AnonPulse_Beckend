using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AnonPulse.Api.Services.Polls;

public sealed class PollService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IPollService
{
    public async Task<PollCreationResult> CreateAsync(
        Guid creatorId,
        CreatePollRequest request,
        CancellationToken cancellationToken = default)
    {
        PollRequestValidation validation = Validate(request);

        if (validation.Errors.Count > 0)
        {
            return new PollCreationResult(null, validation.Errors);
        }

        var poll = new Poll
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Title = validation.Title,
            Status = PollStatus.Draft,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        ReplaceContents(poll, validation);

        dbContext.Polls.Add(poll);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PollCreationResult(ToResponse(poll), validation.Errors);
    }

    public async Task<IReadOnlyList<PollResponse>> GetAllAsync(
        Guid creatorId,
        CancellationToken cancellationToken = default)
    {
        List<Poll> polls = await PollsWithQuestions()
            .Where(poll => poll.CreatorId == creatorId)
            .OrderByDescending(poll => poll.CreatedAt)
            .ThenByDescending(poll => poll.Id)
            .ToListAsync(cancellationToken);

        return polls.Select(ToResponse).ToArray();
    }

    public async Task<PollResponse?> GetByIdAsync(
        Guid creatorId,
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        Poll? poll = await PollsWithQuestions()
            .Where(poll => poll.Id == pollId && poll.CreatorId == creatorId)
            .SingleOrDefaultAsync(cancellationToken);

        return poll is null ? null : ToResponse(poll);
    }

    public async Task<PollUpdateResult> UpdateAsync(
        Guid creatorId,
        Guid pollId,
        CreatePollRequest request,
        CancellationToken cancellationToken = default)
    {
        PollRequestValidation validation = Validate(request);
        if (validation.Errors.Count > 0)
        {
            return new PollUpdateResult(
                PollUpdateOutcome.Invalid,
                ValidationErrors: validation.Errors);
        }

        await using IDbContextTransaction? transaction =
            dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

        Poll? poll = transaction is null
            ? await PollsWithQuestions(tracking: true)
                .SingleOrDefaultAsync(item => item.Id == pollId, cancellationToken)
            : await LockPollAsync(pollId, cancellationToken);

        if (poll is null || poll.CreatorId != creatorId)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new PollUpdateResult(PollUpdateOutcome.NotFound);
        }

        if (poll.Status != PollStatus.Draft)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new PollUpdateResult(
                PollUpdateOutcome.Conflict,
                Error: "Only draft polls can be edited.");
        }

        try
        {
            if (transaction is not null)
            {
                await dbContext.Entry(poll)
                    .Collection(item => item.Questions)
                    .Query()
                    .Include(question => question.Options)
                    .LoadAsync(cancellationToken);
                dbContext.PollOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.Options));
                dbContext.PollQuestions.RemoveRange(poll.Questions);
                poll.Questions.Clear();
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                int updatedPolls = await dbContext.Polls
                    .Where(item => item.Id == pollId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            item => item.Title,
                            validation.Title),
                        cancellationToken);
                if (updatedPolls != 1)
                {
                    await RollbackAsync(transaction, CancellationToken.None);
                    dbContext.ChangeTracker.Clear();
                    return new PollUpdateResult(
                        PollUpdateOutcome.Conflict,
                        Error: "Poll row was not available for update.");
                }

                poll = await dbContext.Polls
                    .SingleAsync(item => item.Id == pollId, cancellationToken);
            }
            else
            {
                dbContext.PollOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.Options));
                dbContext.PollQuestions.RemoveRange(poll.Questions);
                poll.Questions.Clear();
                poll.Title = validation.Title;
            }

            ReplaceContents(poll, validation);
            dbContext.PollQuestions.AddRange(poll.Questions);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, CancellationToken.None);
            dbContext.ChangeTracker.Clear();
            return new PollUpdateResult(
                PollUpdateOutcome.Conflict,
                Error: "Poll state was changed by another request.");
        }
        catch
        {
            await RollbackAsync(transaction, CancellationToken.None);
            throw;
        }

        return new PollUpdateResult(
            PollUpdateOutcome.Succeeded,
            ToResponse(poll));
    }

    public Task<PollTransitionResult> PublishAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            actorId,
            isAdmin,
            pollId,
            PollStatus.Draft,
            PollStatus.Open,
            validateQuestions: true,
            cancellationToken);
    }

    public Task<PollTransitionResult> CloseAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        CancellationToken cancellationToken = default)
    {
        return TransitionAsync(
            actorId,
            isAdmin,
            pollId,
            PollStatus.Open,
            PollStatus.Closed,
            validateQuestions: false,
            cancellationToken);
    }

    private async Task<PollTransitionResult> TransitionAsync(
        Guid actorId,
        bool isAdmin,
        Guid pollId,
        PollStatus expectedStatus,
        PollStatus targetStatus,
        bool validateQuestions,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        Poll? poll;
        if (dbContext.Database.IsRelational())
        {
            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            poll = await LockPollAsync(pollId, cancellationToken);

            if (poll is not null)
            {
                await dbContext.Entry(poll)
                    .Collection(item => item.Questions)
                    .Query()
                    .Include(question => question.Options)
                    .LoadAsync(cancellationToken);
            }
        }
        else
        {
            poll = await PollsWithQuestions(tracking: true)
                .SingleOrDefaultAsync(item => item.Id == pollId, cancellationToken);
        }

        if (poll is null || (!isAdmin && poll.CreatorId != actorId))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            return new PollTransitionResult(PollTransitionOutcome.NotFound);
        }

        if (poll.Status != expectedStatus)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            return Conflict(
                $"Poll cannot transition from '{ToStatusValue(poll.Status)}' " +
                $"to '{ToStatusValue(targetStatus)}'.");
        }

        if (validateQuestions && !HasValidQuestions(poll))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            return Conflict(
                "Poll must contain at least one valid question with at least two " +
                "non-empty unique options before publication.");
        }

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        poll.Status = targetStatus;
        if (targetStatus == PollStatus.Open)
        {
            poll.PublishedAt = utcNow;
        }
        else
        {
            poll.ClosedAt = utcNow;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            dbContext.ChangeTracker.Clear();
            return Conflict("Poll state was changed by another request.");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        return new PollTransitionResult(
            PollTransitionOutcome.Succeeded,
            ToResponse(poll));
    }

    private IQueryable<Poll> PollsWithQuestions(bool tracking = false)
    {
        IQueryable<Poll> polls = dbContext.Polls;
        if (!tracking)
        {
            polls = polls.AsNoTracking();
        }

        return polls
            .Include(poll => poll.Questions)
            .ThenInclude(question => question.Options)
            .AsSplitQuery();
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

    private static async Task RollbackAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private static PollRequestValidation Validate(CreatePollRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? title = NormalizeOptionalWhitespace(request.Title);
        bool usesNewFormat = request.Questions is not null;
        bool usesLegacyFormat = request.Question is not null || request.Options is not null;

        if (usesNewFormat && usesLegacyFormat)
        {
            errors[nameof(request.Questions)] =
                ["Use either 'questions' or legacy 'question' and 'options', not both."];
            return new PollRequestValidation(title, [], errors);
        }

        var questions = new List<ValidatedQuestion>();

        if (usesNewFormat)
        {
            IReadOnlyList<CreatePollQuestionRequest?> requestedQuestions = request.Questions!;
            if (requestedQuestions.Count == 0)
            {
                errors[nameof(request.Questions)] = ["At least one question is required."];
            }

            for (int index = 0; index < requestedQuestions.Count; index++)
            {
                CreatePollQuestionRequest? question = requestedQuestions[index];
                if (question is null)
                {
                    errors[$"Questions[{index}]"] = ["Question must not be null."];
                    continue;
                }

                questions.Add(ValidateQuestion(
                    question.Text,
                    question.Options,
                    question.AllowsMultipleAnswers,
                    $"Questions[{index}]",
                    errors));
            }
        }
        else if (usesLegacyFormat)
        {
            questions.Add(ValidateQuestion(
                request.Question,
                request.Options,
                allowsMultipleAnswers: false,
                string.Empty,
                errors));
        }
        else
        {
            errors[nameof(request.Questions)] = ["At least one question is required."];
        }

        return new PollRequestValidation(title, questions, errors);
    }

    private static ValidatedQuestion ValidateQuestion(
        string? questionText,
        IReadOnlyList<string?>? requestedOptions,
        bool allowsMultipleAnswers,
        string prefix,
        IDictionary<string, string[]> errors)
    {
        string text = NormalizeWhitespace(questionText);
        string textKey = prefix.Length == 0 ? "Question" : $"{prefix}.Text";
        string optionsKey = prefix.Length == 0 ? "Options" : $"{prefix}.Options";

        if (text.Length == 0)
        {
            errors[textKey] = ["Question must not be empty."];
        }

        IReadOnlyList<string?> options = requestedOptions ?? [];
        if (options.Count < 2)
        {
            errors[optionsKey] = ["At least two options are required."];
        }

        var normalizedOptions = new List<string>(options.Count);
        var uniqueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < options.Count; index++)
        {
            string option = NormalizeWhitespace(options[index]);
            normalizedOptions.Add(option);
            string optionKey = $"{optionsKey}[{index}]";

            if (option.Length == 0)
            {
                errors[optionKey] = ["Option text must not be empty."];
                continue;
            }

            if (!uniqueOptions.Add(option))
            {
                errors[optionKey] = ["Options must be unique within a question."];
            }
        }

        return new ValidatedQuestion(text, normalizedOptions, allowsMultipleAnswers);
    }

    private static bool HasValidQuestions(Poll poll)
    {
        return poll.Questions.Count > 0 && poll.Questions.All(question =>
            !string.IsNullOrWhiteSpace(question.Text) &&
            question.Options.Count >= 2 &&
            question.Options.All(option => !string.IsNullOrWhiteSpace(option.Text)) &&
            question.Options
                .Select(option => NormalizeWhitespace(option.Text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == question.Options.Count);
    }

    private static void ReplaceContents(
        Poll poll,
        PollRequestValidation validation)
    {
        for (int questionPosition = 0;
             questionPosition < validation.Questions.Count;
             questionPosition++)
        {
            ValidatedQuestion validatedQuestion = validation.Questions[questionPosition];
            var question = new PollQuestion
            {
                Id = Guid.NewGuid(),
                PollId = poll.Id,
                Text = validatedQuestion.Text,
                Position = questionPosition,
                AllowsMultipleAnswers = validatedQuestion.AllowsMultipleAnswers
            };

            for (int optionPosition = 0;
                 optionPosition < validatedQuestion.Options.Count;
                 optionPosition++)
            {
                question.Options.Add(new PollOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Text = validatedQuestion.Options[optionPosition],
                    Position = optionPosition
                });
            }

            poll.Questions.Add(question);
        }
    }

    private static string NormalizeWhitespace(string? value)
    {
        return value is null
            ? string.Empty
            : string.Join(
                ' ',
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NormalizeOptionalWhitespace(string? value)
    {
        string normalized = NormalizeWhitespace(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static PollResponse ToResponse(Poll poll)
    {
        PollQuestionResponse[] questions = poll.Questions
            .OrderBy(question => question.Position)
            .Select(question => new PollQuestionResponse(
                question.Id,
                question.Text,
                question.Position,
                question.Options
                    .OrderBy(option => option.Position)
                    .Select(option => new PollOptionResponse(
                        option.Id,
                        option.Text,
                        option.Position))
                    .ToArray(),
                question.AllowsMultipleAnswers))
            .ToArray();
        PollQuestionResponse? legacyQuestion = questions.Length == 1 ? questions[0] : null;

        return new PollResponse(
            poll.Id,
            poll.Title,
            ToStatusValue(poll.Status),
            poll.CreatedAt,
            poll.PublishedAt,
            poll.ClosedAt,
            questions,
            legacyQuestion?.Text,
            legacyQuestion?.Options ?? []);
    }

    private static string ToStatusValue(PollStatus status)
    {
        return status switch
        {
            PollStatus.Draft => "draft",
            PollStatus.Open => "open",
            PollStatus.Closed => "closed",
            _ => throw new InvalidOperationException($"Unknown poll status: {status}.")
        };
    }

    private static PollTransitionResult Conflict(string error)
    {
        return new PollTransitionResult(PollTransitionOutcome.Conflict, Error: error);
    }

    private sealed record PollRequestValidation(
        string? Title,
        IReadOnlyList<ValidatedQuestion> Questions,
        IReadOnlyDictionary<string, string[]> Errors);

    private sealed record ValidatedQuestion(
        string Text,
        IReadOnlyList<string> Options,
        bool AllowsMultipleAnswers);
}
