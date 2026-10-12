using AnonPulse.Api.Contracts.Polls;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;

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
                Position = questionPosition
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
        Poll? poll = await PollsWithQuestions(tracking: true)
            .SingleOrDefaultAsync(poll => poll.Id == pollId, cancellationToken);

        if (poll is null || (!isAdmin && poll.CreatorId != actorId))
        {
            return new PollTransitionResult(PollTransitionOutcome.NotFound);
        }

        if (poll.Status != expectedStatus)
        {
            return Conflict(
                $"Poll cannot transition from '{ToStatusValue(poll.Status)}' " +
                $"to '{ToStatusValue(targetStatus)}'.");
        }

        if (validateQuestions && !HasValidQuestions(poll))
        {
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
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return Conflict("Poll state was changed by another request.");
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
            IReadOnlyList<CreatePollQuestionRequest> requestedQuestions = request.Questions!;
            if (requestedQuestions.Count == 0)
            {
                errors[nameof(request.Questions)] = ["At least one question is required."];
            }

            for (int index = 0; index < requestedQuestions.Count; index++)
            {
                CreatePollQuestionRequest question = requestedQuestions[index];
                questions.Add(ValidateQuestion(
                    question.Text,
                    question.Options,
                    $"Questions[{index}]",
                    errors));
            }
        }
        else if (usesLegacyFormat)
        {
            questions.Add(ValidateQuestion(
                request.Question,
                request.Options,
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

        return new ValidatedQuestion(text, normalizedOptions);
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
                    .ToArray()))
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
        IReadOnlyList<string> Options);
}
