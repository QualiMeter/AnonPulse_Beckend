using System.Text.Json;
using AnonPulse.Api.Contracts.FeedbackAnalysis;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IFeedbackAnalysisJobProcessor
{
    Task ProcessAsync(Guid analysisId, CancellationToken cancellationToken);
}

public sealed class FeedbackAnalysisJobProcessor(
    ApplicationDbContext dbContext,
    IFeedbackAnalysisProvider provider,
    IPersonalDataSanitizer sanitizer,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<FeedbackAnalysisJobProcessor> logger) : IFeedbackAnalysisJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AiOptions _options = options.Value;

    public async Task ProcessAsync(Guid analysisId, CancellationToken cancellationToken)
    {
        PollFeedbackAnalysis? analysis = await dbContext.PollFeedbackAnalyses
            .SingleOrDefaultAsync(item => item.Id == analysisId, cancellationToken);
        if (analysis is null || analysis.Status != FeedbackAnalysisStatus.Processing) return;

        try
        {
            List<PollTextAnswer> answers = await dbContext.PollTextAnswers.AsNoTracking()
                .Where(item => item.QuestionId == analysis.QuestionId && item.Text.Trim() != "")
                .OrderBy(item => item.Id).ToListAsync(cancellationToken);
            if (answers.Count != analysis.AnswerCount ||
                FeedbackAnalysisService.CreateFingerprint(answers) != analysis.AnswerFingerprint)
                throw new FeedbackProviderException("answer_set_changed", false);
            int totalCharacters = answers.Sum(item => item.Text.Length);
            int estimatedTokens = (int)Math.Ceiling(totalCharacters / 4d) + 1_000;
            if (answers.Count > _options.MaxAnswers ||
                totalCharacters > _options.MaxTotalCharacters ||
                estimatedTokens > _options.MaxInputTokens)
                throw new FeedbackProviderException("feedback_limit_exceeded", false);

            var inputs = answers.Select((answer, index) => new FeedbackInputAnswer(
                $"answer-{index + 1:D6}-{Guid.NewGuid():N}", sanitizer.Sanitize(answer.Text))).ToList();
            var inputIds = inputs.Select(item => item.TemporaryId).ToHashSet(StringComparer.Ordinal);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            ProviderAnalysisResult providerResult;
            try
            {
                providerResult = await provider.AnalyzeAsync(
                    new FeedbackAnalysisInput(analysis.Model, inputs, _options.MaxOutputTokens),
                    timeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FeedbackProviderException("provider_timeout", true, null, exception);
            }

            FeedbackReportResponse report = ValidateAndBuildReport(providerResult.Report, inputIds,
                analysis.AnswerCount);
            analysis.ReportJson = JsonSerializer.Serialize(report, JsonOptions);
            analysis.PromptTokens = providerResult.PromptTokens;
            analysis.CompletionTokens = providerResult.CompletionTokens;
            analysis.Status = FeedbackAnalysisStatus.Completed;
            analysis.ErrorCode = null;
            analysis.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            analysis.NextAttemptAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            analysis.Status = FeedbackAnalysisStatus.Pending;
            analysis.NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (FeedbackProviderException exception)
        {
            await RecordFailureAsync(analysis, exception.ErrorCode, exception.Retryable,
                exception.RetryAfter, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Feedback analysis job {AnalysisId} failed with an internal error.",
                analysis.Id);
            await RecordFailureAsync(analysis, "analysis_internal_error", true, null,
                cancellationToken);
        }
    }

    private async Task RecordFailureAsync(PollFeedbackAnalysis analysis, string code,
        bool retryable, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        int maximumAttempts = _options.MaxRetries + 1;
        if (retryable && analysis.Attempts < maximumAttempts)
        {
            TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(
                Math.Min(Math.Pow(2, analysis.Attempts) * 5, 3600));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            analysis.Status = FeedbackAnalysisStatus.Pending;
            analysis.NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime + delay;
        }
        else
        {
            analysis.Status = FeedbackAnalysisStatus.Failed;
            analysis.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
            analysis.NextAttemptAt = null;
        }
        analysis.ErrorCode = code;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FeedbackReportResponse ValidateAndBuildReport(
        ProviderReport source, HashSet<string> inputIds, int answerCount)
    {
        if (string.IsNullOrWhiteSpace(source.Summary) || source.Themes is null ||
            source.Recommendations is null || source.Limitations is null)
            throw new FeedbackProviderException("invalid_provider_response", false);

        var themes = new List<FeedbackThemeResponse>();
        foreach (ProviderTheme theme in source.Themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Name) || string.IsNullOrWhiteSpace(theme.Description) ||
                theme.AnswerIds is null ||
                theme.Category is not ("problem" or "positive" or "neutral"))
                throw new FeedbackProviderException("invalid_provider_response", false);
            string[] uniqueIds = theme.AnswerIds.Distinct(StringComparer.Ordinal).ToArray();
            if (uniqueIds.Length == 0 || uniqueIds.Any(id => !inputIds.Contains(id)))
                throw new FeedbackProviderException("invalid_provider_response", false);
            themes.Add(new(theme.Name, theme.Description, theme.Category, uniqueIds.Length));
        }

        var recommendations = source.Recommendations.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Description) ||
                item.Priority is not ("low" or "medium" or "high"))
                throw new FeedbackProviderException("invalid_provider_response", false);
            return new FeedbackRecommendationResponse(item.Title, item.Description, item.Priority);
        }).ToList();
        List<string> limitations = source.Limitations.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        if (answerCount < 5 && !limitations.Any(item => item.Contains("мал", StringComparison.OrdinalIgnoreCase)))
            limitations.Add("Выводы основаны на малом количестве отзывов и могут быть неточными.");
        return new(source.Summary, themes, recommendations, limitations);
    }
}
