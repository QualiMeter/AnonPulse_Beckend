using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnonPulse.Api.Contracts.FeedbackAnalysis;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public sealed class FeedbackAnalysisService(
    ApplicationDbContext dbContext,
    IOptions<AiOptions> options,
    TimeProvider timeProvider) : IFeedbackAnalysisService
{
    private readonly AiOptions _options = options.Value;

    public async Task<FeedbackAnalysisServiceResult> StartAsync(
        Guid actorId, bool isAdmin, Guid pollId, Guid questionId,
        bool externalProcessingConfirmed, CancellationToken cancellationToken)
    {
        PollQuestion? question = await dbContext.PollQuestions
            .Include(item => item.Poll)
            .Include(item => item.TextAnswers)
            .SingleOrDefaultAsync(item => item.Id == questionId && item.PollId == pollId,
                cancellationToken);
        FeedbackAnalysisServiceResult? validation = Validate(actorId, isAdmin, question);
        if (validation is not null)
        {
            return validation;
        }

        if (!externalProcessingConfirmed)
        {
            return new(FeedbackAnalysisOutcome.Invalid, ErrorCode: "external_processing_consent_required",
                Error: "Explicit confirmation of external processing is required.");
        }
        if (!_options.Enabled || !string.Equals(_options.Provider, "Groq", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(_options.Groq.ApiKey))
        {
            return new(FeedbackAnalysisOutcome.Disabled, ErrorCode: "ai_not_configured",
                Error: "Feedback analysis is disabled or not configured.");
        }

        List<PollTextAnswer> answers = question!.TextAnswers
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.Id).ToList();
        if (answers.Count == 0)
        {
            return new(FeedbackAnalysisOutcome.Invalid, ErrorCode: "no_feedback_answers",
                Error: "At least one non-empty feedback answer is required.");
        }
        int totalCharacters = answers.Sum(item => item.Text.Length);
        const int promptAndSchemaTokenAllowance = 1_000;
        int estimatedTokens = (int)Math.Ceiling(totalCharacters / 4d) +
                              promptAndSchemaTokenAllowance;
        if (answers.Count > _options.MaxAnswers || totalCharacters > _options.MaxTotalCharacters ||
            estimatedTokens > _options.MaxInputTokens)
        {
            return new(FeedbackAnalysisOutcome.TooLarge, ErrorCode: "feedback_limit_exceeded",
                Error: "The feedback set exceeds the configured free-tier analysis limits.");
        }

        string fingerprint = CreateFingerprint(answers);
        PollFeedbackAnalysis? existing = await dbContext.PollFeedbackAnalyses
            .SingleOrDefaultAsync(item => item.QuestionId == questionId &&
                                          item.AnswerFingerprint == fingerprint,
                cancellationToken);
        if (existing is not null)
        {
            return new(FeedbackAnalysisOutcome.Succeeded, ToResponse(existing));
        }

        var analysis = new PollFeedbackAnalysis
        {
            Id = Guid.NewGuid(),
            PollId = pollId,
            QuestionId = questionId,
            Provider = _options.Provider,
            Model = _options.Model,
            AlgorithmVersion = "feedback-v1",
            AnswerCount = answers.Count,
            AnswerFingerprint = fingerprint,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime
        };
        dbContext.PollFeedbackAnalyses.Add(analysis);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(analysis).State = EntityState.Detached;
            existing = await dbContext.PollFeedbackAnalyses.SingleAsync(item =>
                item.QuestionId == questionId && item.AnswerFingerprint == fingerprint,
                cancellationToken);
            return new(FeedbackAnalysisOutcome.Succeeded, ToResponse(existing));
        }
        return new(FeedbackAnalysisOutcome.Succeeded, ToResponse(analysis));
    }

    public async Task<FeedbackAnalysisServiceResult> GetAsync(
        Guid actorId, bool isAdmin, Guid pollId, Guid questionId,
        CancellationToken cancellationToken)
    {
        PollQuestion? question = await dbContext.PollQuestions.Include(item => item.Poll)
            .SingleOrDefaultAsync(item => item.Id == questionId && item.PollId == pollId,
                cancellationToken);
        FeedbackAnalysisServiceResult? validation = Validate(actorId, isAdmin, question, requireClosed: false);
        if (validation is not null)
        {
            return validation;
        }
        PollFeedbackAnalysis? analysis = await dbContext.PollFeedbackAnalyses
            .Where(item => item.PollId == pollId && item.QuestionId == questionId)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return analysis is null
            ? new(FeedbackAnalysisOutcome.NotFound, ErrorCode: "analysis_not_found",
                Error: "Feedback analysis was not found.")
            : new(FeedbackAnalysisOutcome.Succeeded, ToResponse(analysis));
    }

    private static FeedbackAnalysisServiceResult? Validate(
        Guid actorId, bool isAdmin, PollQuestion? question, bool requireClosed = true)
    {
        if (question is null) return new(FeedbackAnalysisOutcome.NotFound, ErrorCode: "question_not_found");
        if (!isAdmin && question.Poll.CreatorId != actorId)
            return new(FeedbackAnalysisOutcome.Forbidden, ErrorCode: "poll_access_forbidden");
        if (requireClosed && question.Poll.Status != PollStatus.Closed)
            return new(FeedbackAnalysisOutcome.Conflict, ErrorCode: "poll_not_closed",
                Error: "Feedback can be analyzed only after the poll is closed.");
        if (question.Type != PollQuestionType.Text)
            return new(FeedbackAnalysisOutcome.Invalid, ErrorCode: "question_not_text",
                Error: "Only text questions can be analyzed.");
        if (question.TextPurpose == TextQuestionPurpose.Knowledge)
            return new(FeedbackAnalysisOutcome.Invalid, ErrorCode: "knowledge_analysis_not_supported",
                Error: "Knowledge evaluation will be implemented in a later release.");
        if (question.TextPurpose != TextQuestionPurpose.Feedback)
            return new(FeedbackAnalysisOutcome.Invalid, ErrorCode: "question_not_feedback");
        return null;
    }

    internal static string CreateFingerprint(IEnumerable<PollTextAnswer> answers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PollTextAnswer answer in answers)
        {
            byte[] data = Encoding.UTF8.GetBytes($"{answer.Id:N}:{answer.Text.Length}:{answer.Text}\n");
            hash.AppendData(data);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static FeedbackAnalysisResponse ToResponse(PollFeedbackAnalysis analysis)
    {
        FeedbackReportResponse? report = analysis.ReportJson is null ? null :
            JsonSerializer.Deserialize<FeedbackReportResponse>(analysis.ReportJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(analysis.Id, analysis.PollId, analysis.QuestionId,
            analysis.Status.ToString().ToLowerInvariant(), analysis.AnswerCount,
            report, analysis.ErrorCode, analysis.CreatedAt, analysis.StartedAt,
            analysis.CompletedAt);
    }
}
