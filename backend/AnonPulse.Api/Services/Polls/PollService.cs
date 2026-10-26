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
        PollRequestValidation validation = Validate(
            request,
            PollKind.Survey,
            fallbackTestScale: null,
            PollResponseMode.Anonymous);

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
            ResponseMode = validation.RequestedResponseMode ?? PollResponseMode.Anonymous,
            Kind = validation.Kind,
            TestScale = validation.TestScale,
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

        PollRequestValidation validation = Validate(
            request,
            poll.Kind,
            poll.TestScale,
            poll.ResponseMode);
        if (validation.Errors.Count > 0)
        {
            await RollbackAsync(transaction, cancellationToken);
            return new PollUpdateResult(
                PollUpdateOutcome.Invalid,
                ValidationErrors: validation.Errors);
        }

        PollResponseMode updatedResponseMode =
            validation.RequestedResponseMode ?? poll.ResponseMode;

        try
        {
            if (transaction is not null)
            {
                await dbContext.Entry(poll)
                    .Collection(item => item.Questions)
                    .Query()
                    .Include(question => question.Options)
                    .Include(question => question.KnowledgeSettings)
                    .Include(question => question.CorrectOptions)
                    .LoadAsync(cancellationToken);
                dbContext.PollQuestionCorrectOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.CorrectOptions));
                dbContext.PollQuestionKnowledgeSettings.RemoveRange(
                    poll.Questions.Where(question => question.KnowledgeSettings is not null)
                        .Select(question => question.KnowledgeSettings!));
                dbContext.PollOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.Options));
                dbContext.PollQuestions.RemoveRange(poll.Questions);
                poll.Questions.Clear();
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                int updatedPolls = await dbContext.Polls
                    .Where(item => item.Id == pollId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(item => item.Title, validation.Title)
                            .SetProperty(item => item.ResponseMode, updatedResponseMode)
                            .SetProperty(item => item.Kind, validation.Kind)
                            .SetProperty(item => item.TestScale, validation.TestScale),
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
                dbContext.PollQuestionKnowledgeSettings.RemoveRange(
                    poll.Questions.Where(question => question.KnowledgeSettings is not null)
                        .Select(question => question.KnowledgeSettings!));
                dbContext.PollQuestionCorrectOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.CorrectOptions));
                dbContext.PollOptions.RemoveRange(
                    poll.Questions.SelectMany(question => question.Options));
                dbContext.PollQuestions.RemoveRange(poll.Questions);
                poll.Questions.Clear();
                poll.Title = validation.Title;
                poll.ResponseMode = updatedResponseMode;
                poll.Kind = validation.Kind;
                poll.TestScale = validation.TestScale;
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
                    .Include(question => question.KnowledgeSettings)
                    .Include(question => question.CorrectOptions)
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
                "Poll must contain at least one valid question; choice questions require " +
                "at least two non-empty unique options and text questions require a purpose.");
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
            .Include(poll => poll.Questions)
            .ThenInclude(question => question.KnowledgeSettings)
            .Include(poll => poll.Questions)
            .ThenInclude(question => question.CorrectOptions)
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

    private static PollRequestValidation Validate(
        CreatePollRequest request,
        PollKind fallbackKind,
        int? fallbackTestScale,
        PollResponseMode fallbackResponseMode)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? title = NormalizeOptionalWhitespace(request.Title);
        PollResponseMode? requestedResponseMode =
            request.ResponseMode?.Trim().ToLowerInvariant() switch
            {
                null => null,
                "anonymous" => PollResponseMode.Anonymous,
                "identified" => PollResponseMode.Identified,
                _ => null
            };
        if (request.ResponseMode?.Trim().ToLowerInvariant() is not
            (null or "anonymous" or "identified"))
        {
            errors[nameof(request.ResponseMode)] =
                ["ResponseMode must be 'anonymous' or 'identified'."];
        }
        PollKind kind = request.Kind?.Trim().ToLowerInvariant() switch
        {
            null => fallbackKind,
            "survey" => PollKind.Survey,
            "test" => PollKind.Test,
            _ => fallbackKind
        };
        if (request.Kind?.Trim().ToLowerInvariant() is not (null or "survey" or "test"))
        {
            errors[nameof(request.Kind)] = ["Kind must be 'survey' or 'test'."];
        }
        int? testScale = request.TestScale ?? (kind == fallbackKind ? fallbackTestScale : null);
        PollResponseMode effectiveResponseMode = requestedResponseMode ?? fallbackResponseMode;
        if (kind == PollKind.Test)
        {
            if (effectiveResponseMode != PollResponseMode.Identified)
            {
                errors[nameof(request.ResponseMode)] =
                    ["A test must use responseMode 'identified'."];
            }
            if (testScale is not (5 or 10 or 12 or 100))
            {
                errors[nameof(request.TestScale)] =
                    ["TestScale must be 5, 10, 12, or 100 for a test."];
            }
        }
        else if (request.TestScale is not null)
        {
            errors[nameof(request.TestScale)] = ["TestScale is allowed only for a test."];
        }
        bool usesNewFormat = request.Questions is not null;
        bool usesLegacyFormat = request.Question is not null || request.Options is not null;

        if (usesNewFormat && usesLegacyFormat)
        {
            errors[nameof(request.Questions)] =
                ["Use either 'questions' or legacy 'question' and 'options', not both."];
            return new PollRequestValidation(
                title, requestedResponseMode, kind, testScale, [], errors);
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
                    question.Type,
                    question.TextPurpose,
                    question.KnowledgeSettings,
                    question.Weight,
                    question.CorrectOptionIndexes,
                    kind,
                    $"Questions[{index}]",
                    errors));
            }
        }
        else if (usesLegacyFormat)
        {
            questions.Add(ValidateQuestion(
                request.Question,
                request.Options,
                allowsMultipleAnswers: null,
                type: null,
                textPurpose: null,
                knowledgeSettings: null,
                weight: null,
                correctOptionIndexes: null,
                kind,
                string.Empty,
                errors));
        }
        else
        {
            errors[nameof(request.Questions)] = ["At least one question is required."];
        }

        return new PollRequestValidation(
            title, requestedResponseMode, kind, kind == PollKind.Test ? testScale : null,
            questions, errors);
    }

    private static ValidatedQuestion ValidateQuestion(
        string? questionText,
        IReadOnlyList<string?>? requestedOptions,
        bool? allowsMultipleAnswers,
        string? type,
        string? textPurpose,
        KnowledgeQuestionSettingsRequest? knowledgeSettings,
        decimal? weight,
        IReadOnlyList<int>? correctOptionIndexes,
        PollKind kind,
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

        PollQuestionType resolvedType = ResolveType(
            type, allowsMultipleAnswers, prefix, errors);
        string purposeKey = prefix.Length == 0 ? "TextPurpose" : $"{prefix}.TextPurpose";
        string settingsKey = prefix.Length == 0 ? "KnowledgeSettings" : $"{prefix}.KnowledgeSettings";
        TextQuestionPurpose? resolvedPurpose = null;
        ValidatedKnowledgeSettings? validatedSettings = null;
        IReadOnlyList<string?> options = requestedOptions ?? [];

        if (resolvedType == PollQuestionType.Text)
        {
            if (options.Count > 0)
            {
                errors[optionsKey] = ["Text questions must not contain options."];
            }

            resolvedPurpose = textPurpose?.Trim().ToLowerInvariant() switch
            {
                "feedback" => TextQuestionPurpose.Feedback,
                "knowledge" => TextQuestionPurpose.Knowledge,
                _ => null
            };
            if (resolvedPurpose is null)
            {
                errors[purposeKey] = ["TextPurpose must be 'feedback' or 'knowledge'."];
            }

            if (resolvedPurpose == TextQuestionPurpose.Knowledge)
            {
                validatedSettings = ValidateKnowledgeSettings(knowledgeSettings, settingsKey, errors);
            }
            else if (knowledgeSettings is not null)
            {
                errors[settingsKey] = ["KnowledgeSettings are allowed only for knowledge questions."];
            }
            if (weight is not null || correctOptionIndexes is not null)
            {
                errors[$"{prefix}.Weight"] =
                    ["Weight and CorrectOptionIndexes are allowed only for choice questions."];
            }
            if (kind == PollKind.Test && resolvedPurpose != TextQuestionPurpose.Knowledge)
            {
                errors[purposeKey] = ["A test text question must use textPurpose 'knowledge'."];
            }
        }
        else
        {
            if (textPurpose is not null || knowledgeSettings is not null)
            {
                errors[purposeKey] = ["Text settings are allowed only for text questions."];
            }
            if (options.Count < 2)
            {
                errors[optionsKey] = ["At least two options are required."];
            }
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

        var correctIndexes = correctOptionIndexes?.ToArray() ?? [];
        string correctKey = prefix.Length == 0
            ? "CorrectOptionIndexes"
            : $"{prefix}.CorrectOptionIndexes";
        string weightKey = prefix.Length == 0 ? "Weight" : $"{prefix}.Weight";
        if (kind == PollKind.Test && resolvedType != PollQuestionType.Text)
        {
            if (weight is null or <= 0 or > 10000)
            {
                errors[weightKey] = ["Weight must be greater than 0 and at most 10000."];
            }
            else if (decimal.Round(weight.Value, 2) != weight.Value)
            {
                errors[weightKey] =
                    ["Weight must have at most 2 decimal places."];
            }
            if (correctIndexes.Distinct().Count() != correctIndexes.Length)
            {
                errors[correctKey] = ["CorrectOptionIndexes must be unique."];
            }
            if (correctIndexes.Any(index => index < 0 || index >= normalizedOptions.Count))
            {
                errors[correctKey] = ["CorrectOptionIndexes contains an out-of-range index."];
            }
            if (resolvedType == PollQuestionType.SingleChoice && correctIndexes.Length != 1)
            {
                errors[correctKey] =
                    ["A single_choice question requires exactly one correct option."];
            }
            if (resolvedType == PollQuestionType.MultipleChoice && correctIndexes.Length == 0)
            {
                errors[correctKey] =
                    ["A multiple_choice question requires one or more correct options."];
            }
        }
        else if (kind == PollKind.Survey &&
                 (weight is not null || correctOptionIndexes is not null))
        {
            errors[weightKey] = ["Test grading settings are allowed only for a test."];
        }

        return new ValidatedQuestion(
            text, normalizedOptions, resolvedType, resolvedPurpose, validatedSettings,
            kind == PollKind.Test && resolvedType != PollQuestionType.Text ? weight : null,
            correctIndexes);
    }

    private static PollQuestionType ResolveType(
        string? type,
        bool? allowsMultipleAnswers,
        string prefix,
        IDictionary<string, string[]> errors)
    {
        string key = prefix.Length == 0 ? "Type" : $"{prefix}.Type";
        PollQuestionType resolved;
        if (type is null)
        {
            return allowsMultipleAnswers == true
                ? PollQuestionType.MultipleChoice
                : PollQuestionType.SingleChoice;
        }

        resolved = type.Trim().ToLowerInvariant() switch
        {
            "single_choice" => PollQuestionType.SingleChoice,
            "multiple_choice" => PollQuestionType.MultipleChoice,
            "text" => PollQuestionType.Text,
            _ => PollQuestionType.SingleChoice
        };
        if (type.Trim().ToLowerInvariant() is not ("single_choice" or "multiple_choice" or "text"))
        {
            errors[key] = ["Type must be 'single_choice', 'multiple_choice', or 'text'."];
            return resolved;
        }

        bool expectedLegacy = resolved == PollQuestionType.MultipleChoice;
        if (allowsMultipleAnswers is not null && allowsMultipleAnswers != expectedLegacy)
        {
            errors[key] = ["Type conflicts with allowsMultipleAnswers."];
        }
        return resolved;
    }

    private static ValidatedKnowledgeSettings? ValidateKnowledgeSettings(
        KnowledgeQuestionSettingsRequest? settings,
        string key,
        IDictionary<string, string[]> errors)
    {
        if (settings is null)
        {
            errors[key] = ["KnowledgeSettings are required for knowledge questions."];
            return null;
        }
        string expected = settings.ExpectedAnswer?.Trim() ?? string.Empty;
        string criteria = settings.GradingCriteria?.Trim() ?? string.Empty;
        if (expected.Length is 0 or > 4000)
        {
            errors[$"{key}.ExpectedAnswer"] = ["ExpectedAnswer must contain 1 to 4000 characters."];
        }
        if (criteria.Length is 0 or > 4000)
        {
            errors[$"{key}.GradingCriteria"] = ["GradingCriteria must contain 1 to 4000 characters."];
        }
        if (settings.MaxScore is null or < 1 or > 100)
        {
            errors[$"{key}.MaxScore"] = ["MaxScore must be between 1 and 100."];
        }
        return new ValidatedKnowledgeSettings(expected, criteria, settings.MaxScore ?? 0);
    }

    private static bool HasValidQuestions(Poll poll)
    {
        return poll.Questions.Count > 0 && poll.Questions.All(question =>
            !string.IsNullOrWhiteSpace(question.Text) &&
            (question.Type == PollQuestionType.Text
                ? question.TextPurpose is not null && question.Options.Count == 0 &&
                  (question.TextPurpose != TextQuestionPurpose.Knowledge ||
                   question.KnowledgeSettings is not null)
                : question.Options.Count >= 2 &&
                  question.Options.All(option => !string.IsNullOrWhiteSpace(option.Text)) &&
                  question.Options.Select(option => NormalizeWhitespace(option.Text))
                      .Distinct(StringComparer.OrdinalIgnoreCase).Count() == question.Options.Count)) &&
               (poll.Kind != PollKind.Test ||
                poll.ResponseMode == PollResponseMode.Identified &&
                poll.TestScale is 5 or 10 or 12 or 100 &&
                poll.Questions.All(question => question.Type == PollQuestionType.Text
                    ? question.TextPurpose == TextQuestionPurpose.Knowledge
                    : question.TestWeight > 0 &&
                      (question.Type == PollQuestionType.SingleChoice
                          ? question.CorrectOptions.Count == 1
                          : question.CorrectOptions.Count > 0)));
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
                Type = validatedQuestion.Type,
                AllowsMultipleAnswers = validatedQuestion.Type == PollQuestionType.MultipleChoice,
                TextPurpose = validatedQuestion.TextPurpose,
                TestWeight = validatedQuestion.TestWeight
            };

            if (validatedQuestion.KnowledgeSettings is not null)
            {
                question.KnowledgeSettings = new PollQuestionKnowledgeSettings
                {
                    QuestionId = question.Id,
                    ExpectedAnswer = validatedQuestion.KnowledgeSettings.ExpectedAnswer,
                    GradingCriteria = validatedQuestion.KnowledgeSettings.GradingCriteria,
                    MaxScore = validatedQuestion.KnowledgeSettings.MaxScore
                };
            }

            for (int optionPosition = 0;
                 optionPosition < validatedQuestion.Options.Count;
                 optionPosition++)
            {
                var option = new PollOption
                {
                    Id = Guid.NewGuid(),
                    QuestionId = question.Id,
                    Text = validatedQuestion.Options[optionPosition],
                    Position = optionPosition
                };
                question.Options.Add(option);
                if (validatedQuestion.CorrectOptionIndexes.Contains(optionPosition))
                {
                    question.CorrectOptions.Add(new PollQuestionCorrectOption
                    {
                        QuestionId = question.Id,
                        OptionId = option.Id,
                        Question = question,
                        Option = option
                    });
                }
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
                question.Type == PollQuestionType.MultipleChoice,
                question.Type.ToApiValue(),
                question.TextPurpose.ToApiValue(),
                question.KnowledgeSettings is null ? null : new KnowledgeQuestionSettingsResponse(
                    question.KnowledgeSettings.ExpectedAnswer,
                    question.KnowledgeSettings.GradingCriteria,
                    question.KnowledgeSettings.MaxScore),
                question.TestWeight,
                question.CorrectOptions.Select(item => item.OptionId).Order().ToArray()))
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
            legacyQuestion?.Options ?? [],
            poll.ResponseMode.ToApiValue(),
            poll.Kind.ToApiValue(),
            poll.TestScale);
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
        PollResponseMode? RequestedResponseMode,
        PollKind Kind,
        int? TestScale,
        IReadOnlyList<ValidatedQuestion> Questions,
        IReadOnlyDictionary<string, string[]> Errors);

    private sealed record ValidatedQuestion(
        string Text,
        IReadOnlyList<string> Options,
        PollQuestionType Type,
        TextQuestionPurpose? TextPurpose,
        ValidatedKnowledgeSettings? KnowledgeSettings,
        decimal? TestWeight,
        IReadOnlyList<int> CorrectOptionIndexes);

    private sealed record ValidatedKnowledgeSettings(
        string ExpectedAnswer,
        string GradingCriteria,
        int MaxScore);
}
