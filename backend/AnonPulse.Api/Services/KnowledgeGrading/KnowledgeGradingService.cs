using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnonPulse.Api.Contracts.KnowledgeGrading;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public sealed class KnowledgeGradingService(
    ApplicationDbContext dbContext,
    IPersonalDataSanitizer sanitizer,
    IOptions<AiOptions> options,
    TimeProvider timeProvider) : IKnowledgeGradingService
{
    internal const string AlgorithmVersion = "knowledge-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AiOptions _options = options.Value;

    public async Task<KnowledgeGradingServiceResult> StartAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, bool externalProcessingConfirmed,
        CancellationToken cancellationToken)
    {
        PollQuestion? question = await dbContext.PollQuestions
            .Include(item => item.Poll)
            .Include(item => item.KnowledgeSettings)
            .SingleOrDefaultAsync(item => item.Id == questionId && item.PollId == pollId,
                cancellationToken);
        KnowledgeGradingServiceResult? invalid = Validate(actorId, isAdmin, question, true);
        if (invalid is not null) return invalid;
        if (!externalProcessingConfirmed)
            return Reject("external_processing_consent_required",
                "Explicit confirmation of external processing is required.");
        if (!_options.Enabled || !_options.KnowledgeGradingEnabled ||
            !string.Equals(_options.Provider, "Groq", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(_options.Groq.ApiKey))
            return new(KnowledgeGradingOutcome.Disabled, ErrorCode: "knowledge_grading_disabled",
                Error: "Knowledge grading is disabled or not configured.");

        PollQuestionKnowledgeSettings settings = question!.KnowledgeSettings!;
        List<NamedAttemptAnswer> answers = await dbContext.NamedAttemptAnswers.AsNoTracking()
            .Where(item => item.QuestionId == questionId &&
                           item.GradingState == TestAnswerGradingStates.Pending &&
                           item.Text != null)
            .OrderBy(item => item.Id).ToListAsync(cancellationToken);
        if (answers.Count == 0)
            return Reject("no_pending_answers", "No pending text answers require grading.");

        string fingerprint = CreateFingerprint(question, settings, answers,
            _options.KnowledgeGradingModel, AlgorithmVersion);
        PollKnowledgeGradingJob? existing = await dbContext.PollKnowledgeGradingJobs
            .SingleOrDefaultAsync(item => item.QuestionId == questionId &&
                                          item.SnapshotFingerprint == fingerprint,
                cancellationToken);
        if (existing is not null && existing.Status != KnowledgeGradingJobStatus.Failed)
            return Success(existing);
        if (existing is not null)
            return await RestartFailedAsync(existing, cancellationToken);

        List<KnowledgeAnswerSnapshot> snapshot = answers.Select(item =>
            new KnowledgeAnswerSnapshot(item.Id, sanitizer.Sanitize(item.Text!))).ToList();
        int totalCharacters = settings.ExpectedAnswer.Length + settings.GradingCriteria.Length +
                              snapshot.Sum(item => item.Text.Length);
        var estimateInput = new KnowledgeGradingInput(_options.KnowledgeGradingModel,
            settings.ExpectedAnswer, settings.GradingCriteria, settings.MaxScore,
            snapshot.Select((item, index) => new KnowledgeInputAnswer(
                $"answer-{index + 1:D6}-{new string('0', 32)}", item.Text)).ToArray(),
            _options.KnowledgeMaxOutputTokens);
        int estimatedInputTokens = GroqKnowledgeGradingProvider.EstimateInputTokens(estimateInput);
        if (answers.Count > _options.KnowledgeMaxAnswers ||
            totalCharacters > _options.KnowledgeMaxTotalCharacters ||
            estimatedInputTokens > _options.KnowledgeMaxInputTokens ||
            estimatedInputTokens + _options.KnowledgeMaxOutputTokens >
            _options.KnowledgeMaxReservedTokens)
            return new(KnowledgeGradingOutcome.TooLarge,
                ErrorCode: "knowledge_grading_limit_exceeded",
                Error: "The answer set exceeds the configured knowledge-grading budget.");

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        var job = new PollKnowledgeGradingJob
        {
            Id = Guid.NewGuid(),
            PollId = pollId,
            QuestionId = questionId,
            Provider = _options.Provider,
            Model = _options.KnowledgeGradingModel,
            AlgorithmVersion = AlgorithmVersion,
            SnapshotFingerprint = fingerprint,
            ExpectedAnswerSnapshot = settings.ExpectedAnswer,
            GradingCriteriaSnapshot = settings.GradingCriteria,
            MaxScoreSnapshot = settings.MaxScore,
            AnswersSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            AnswerCount = snapshot.Count,
            ReservedInputTokens = estimatedInputTokens,
            ReservedOutputTokens = _options.KnowledgeMaxOutputTokens,
            CreatedAt = now,
            NextAttemptAt = now
        };
        dbContext.PollKnowledgeGradingJobs.Add(job);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(job).State = EntityState.Detached;
            existing = await dbContext.PollKnowledgeGradingJobs.SingleAsync(item =>
                item.QuestionId == questionId && item.SnapshotFingerprint == fingerprint,
                cancellationToken);
            return Success(existing);
        }
        return Success(job);
    }

    public async Task<KnowledgeGradingServiceResult> GetAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, CancellationToken cancellationToken)
    {
        KnowledgeGradingServiceResult? invalid = await AuthorizeAsync(actorId, isAdmin,
            pollId, questionId, cancellationToken);
        if (invalid is not null) return invalid;
        PollKnowledgeGradingJob? job = await dbContext.PollKnowledgeGradingJobs.AsNoTracking()
            .Where(item => item.PollId == pollId && item.QuestionId == questionId)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return job is null
            ? new(KnowledgeGradingOutcome.NotFound, ErrorCode: "knowledge_grading_not_found")
            : Success(job);
    }

    public async Task<KnowledgeGradingServiceResult> GetResultsAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        KnowledgeGradingServiceResult? invalid = await AuthorizeAsync(actorId, isAdmin,
            pollId, questionId, cancellationToken);
        if (invalid is not null) return invalid;
        PollKnowledgeGradingJob? job = await dbContext.PollKnowledgeGradingJobs.AsNoTracking()
            .Where(item => item.PollId == pollId && item.QuestionId == questionId)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (job is null) return new(KnowledgeGradingOutcome.NotFound,
            ErrorCode: "knowledge_grading_not_found");
        if (job.Status != KnowledgeGradingJobStatus.Completed)
            return new(KnowledgeGradingOutcome.Conflict,
                ErrorCode: "knowledge_grading_not_completed",
                Error: "Knowledge grading results are not ready.");

        int total = await dbContext.PollKnowledgeGradingSuggestions.AsNoTracking()
            .CountAsync(item => item.JobId == job.Id, cancellationToken);
        long offset = ((long)page - 1) * pageSize;
        PollKnowledgeGradingSuggestion[] rows = offset >= total ? [] : await dbContext
            .PollKnowledgeGradingSuggestions.AsNoTracking()
            .Where(item => item.JobId == job.Id).OrderBy(item => item.AnswerId)
            .Skip((int)offset).Take(pageSize).ToArrayAsync(cancellationToken);
        var items = rows.Select(item => new KnowledgeGradingSuggestionResponse(
            item.AnswerId, item.SuggestedScore, item.MaxScore, item.Explanation,
            ParseArray(item.CorrectPointsJson), ParseArray(item.MissingOrIncorrectPointsJson),
            item.NeedsReview, ParseArray(item.ReviewReasonsJson), job.Model,
            job.AlgorithmVersion)).ToArray();
        return new(KnowledgeGradingOutcome.Succeeded, Results:
            new(job.Id, job.Model, job.AlgorithmVersion, items, page, pageSize, total,
                total == 0 ? 0 : ((total - 1) / pageSize) + 1));
    }

    private async Task<KnowledgeGradingServiceResult?> AuthorizeAsync(Guid actorId, bool isAdmin,
        Guid pollId, Guid questionId, CancellationToken cancellationToken)
    {
        PollQuestion? question = await dbContext.PollQuestions.Include(item => item.Poll)
            .Include(item => item.KnowledgeSettings)
            .SingleOrDefaultAsync(item => item.Id == questionId && item.PollId == pollId,
                cancellationToken);
        return Validate(actorId, isAdmin, question, false);
    }

    private static KnowledgeGradingServiceResult? Validate(Guid actorId, bool isAdmin,
        PollQuestion? question, bool requireClosed)
    {
        if (question is null) return new(KnowledgeGradingOutcome.NotFound,
            ErrorCode: "question_not_found");
        if (!isAdmin && question.Poll.CreatorId != actorId)
            return new(KnowledgeGradingOutcome.Forbidden, ErrorCode: "poll_access_forbidden");
        if (question.Poll.Kind != PollKind.Test)
            return Reject("poll_not_test", "Knowledge grading is available only for tests.");
        if (requireClosed && question.Poll.Status != PollStatus.Closed)
            return new(KnowledgeGradingOutcome.Conflict, ErrorCode: "test_not_closed",
                Error: "Knowledge grading can start only after the test is closed.");
        if (question.Type != PollQuestionType.Text ||
            question.TextPurpose != TextQuestionPurpose.Knowledge)
            return Reject("question_not_knowledge_text",
                "Only knowledge-purpose text questions can be graded.");
        if (question.KnowledgeSettings is null ||
            string.IsNullOrWhiteSpace(question.KnowledgeSettings.ExpectedAnswer) ||
            string.IsNullOrWhiteSpace(question.KnowledgeSettings.GradingCriteria) ||
            question.KnowledgeSettings.MaxScore is < 1 or > 100)
            return Reject("invalid_knowledge_settings", "Knowledge grading settings are invalid.");
        return null;
    }

    private async Task<KnowledgeGradingServiceResult> RestartFailedAsync(
        PollKnowledgeGradingJob job, CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        if (job.NextAttemptAt is DateTime retryAt && retryAt > now)
            return new(KnowledgeGradingOutcome.RateLimited,
                ErrorCode: "knowledge_grading_retry_cooldown",
                Error: "Knowledge grading cannot be restarted yet.",
                RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((retryAt - now).TotalSeconds)));
        job.Status = KnowledgeGradingJobStatus.Pending;
        job.StartedAt = null; job.CompletedAt = null; job.NextAttemptAt = now;
        job.Attempts = 0; job.ErrorCode = null; job.PromptTokens = null;
        job.CompletionTokens = null; job.SuggestedCount = 0;
        if (dbContext.Database.IsRelational())
            await dbContext.PollKnowledgeGradingSuggestions.Where(item => item.JobId == job.Id)
                .ExecuteDeleteAsync(cancellationToken);
        else
            dbContext.PollKnowledgeGradingSuggestions.RemoveRange(
                dbContext.PollKnowledgeGradingSuggestions.Where(item => item.JobId == job.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Success(job);
    }

    internal static string CreateFingerprint(PollQuestion question,
        PollQuestionKnowledgeSettings settings, IEnumerable<NamedAttemptAnswer> answers,
        string model, string algorithm)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(question.PollId.ToString("N")); Add(question.Id.ToString("N"));
        Add(model); Add(algorithm); Add(settings.ExpectedAnswer); Add(settings.GradingCriteria);
        Add(settings.MaxScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (NamedAttemptAnswer answer in answers.OrderBy(item => item.Id))
            Add($"{answer.Id:N}:{answer.Text!.Length}:{answer.Text}");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static KnowledgeGradingJobResponse ToResponse(PollKnowledgeGradingJob job) =>
        new(job.Id, job.PollId, job.QuestionId, job.Status.ToString().ToLowerInvariant(),
            job.AnswerCount, job.SuggestedCount, job.CreatedAt, job.StartedAt,
            job.CompletedAt, job.ErrorCode, job.Model, job.AlgorithmVersion);

    private static KnowledgeGradingServiceResult Success(PollKnowledgeGradingJob job) =>
        new(KnowledgeGradingOutcome.Succeeded, ToResponse(job));
    private static KnowledgeGradingServiceResult Reject(string code, string error) =>
        new(KnowledgeGradingOutcome.Invalid, ErrorCode: code, Error: error);
    private static string[] ParseArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
}

public sealed record KnowledgeAnswerSnapshot(Guid AnswerId, string Text);
