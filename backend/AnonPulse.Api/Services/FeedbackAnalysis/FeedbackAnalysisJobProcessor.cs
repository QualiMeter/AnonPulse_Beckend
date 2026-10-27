using System.Data;
using System.Text.Json;
using AnonPulse.Api.Contracts.FeedbackAnalysis;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IFeedbackAnalysisJobProcessor
{
    Task ProcessAsync(FeedbackAnalysisLease lease, CancellationToken cancellationToken);
}

public sealed class FeedbackAnalysisJobProcessor(
    ApplicationDbContext dbContext,
    IFeedbackAnalysisProvider provider,
    IPersonalDataSanitizer sanitizer,
    IAiTokenBudgetManager budget,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<FeedbackAnalysisJobProcessor> logger) : IFeedbackAnalysisJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AiOptions _options = options.Value;

    public async Task ProcessAsync(FeedbackAnalysisLease lease,
        CancellationToken cancellationToken)
    {
        PollFeedbackAnalysis? analysis = await dbContext.PollFeedbackAnalyses
            .SingleOrDefaultAsync(item => item.Id == lease.AnalysisId &&
                item.Status == FeedbackAnalysisStatus.Processing &&
                item.LeaseId == lease.LeaseId && item.LeaseGeneration == lease.Generation,
                cancellationToken);
        if (analysis is null) return;
        Guid? activeReservationId = null;

        try
        {
            List<PollTextAnswer> answers = await dbContext.PollTextAnswers.AsNoTracking()
                .Where(item => item.QuestionId == analysis.QuestionId && item.Text.Trim() != "")
                .OrderBy(item => item.Id).ToListAsync(cancellationToken);
            if (answers.Count != analysis.AnswerCount ||
                FeedbackAnalysisService.CreateFingerprint(answers) != analysis.AnswerFingerprint)
                throw new FeedbackProviderException("answer_set_changed", false);
            int totalCharacters = answers.Sum(item => item.Text.Length);
            int estimatedTokens = GroqFeedbackAnalysisProvider.EstimateInputTokens(
                analysis.Model, answers.Select(item => sanitizer.Sanitize(item.Text)).ToList(),
                _options.MaxOutputTokens, _options.ReasoningEffort);
            if (answers.Count > _options.MaxAnswers ||
                totalCharacters > _options.MaxTotalCharacters ||
                estimatedTokens > _options.MaxInputTokens)
                throw new FeedbackProviderException("feedback_limit_exceeded", false);

            var inputs = answers.Select((answer, index) => new FeedbackInputAnswer(
                $"answer-{index + 1:D6}-{Guid.NewGuid():N}", sanitizer.Sanitize(answer.Text))).ToList();
            var inputIds = inputs.Select(item => item.TemporaryId).ToHashSet(StringComparer.Ordinal);

            int budgetAttempt = analysis.LeaseGeneration > 0
                ? analysis.LeaseGeneration : Math.Max(1, analysis.Attempts);
            AiBudgetReservationResult reservation = await budget.ReserveAsync(
                "feedback_analysis", analysis.Id, budgetAttempt,
                checked(estimatedTokens + _options.MaxOutputTokens), cancellationToken);
            if (!reservation.Succeeded)
                throw new FeedbackProviderException("ai_period_budget_exhausted", true,
                    TimeSpan.FromSeconds(reservation.RetryAfterSeconds ?? 60));
            if (reservation.AlreadyExists) return;
            activeReservationId = reservation.ReservationId;

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
                await budget.SettleAsync(reservation.ReservationId!.Value, null, null, true,
                    CancellationToken.None);
                activeReservationId = null;
                throw new FeedbackProviderException("provider_timeout", true, null, exception);
            }
            catch
            {
                await budget.SettleAsync(reservation.ReservationId!.Value, null, null, true,
                    CancellationToken.None);
                activeReservationId = null;
                throw;
            }
            await budget.SettleAsync(reservation.ReservationId!.Value,
                providerResult.PromptTokens, providerResult.CompletionTokens, false,
                CancellationToken.None);
            activeReservationId = null;

            FeedbackReportResponse report = ValidateAndBuildReport(providerResult.Report, inputIds,
                analysis.AnswerCount);
            await PersistSuccessAsync(lease, report, providerResult, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeReservationId is Guid held)
                await budget.SettleAsync(held, null, null, true, CancellationToken.None);
            await ReturnToQueueAsync(lease, CancellationToken.None);
            throw;
        }
        catch (FeedbackProviderException exception)
        {
            await RecordFailureAsync(lease, exception.ErrorCode, exception.Retryable,
                exception.RetryAfter, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Feedback analysis job {AnalysisId} failed with an internal error.",
                lease.AnalysisId);
            await RecordFailureAsync(lease, "analysis_internal_error", true, null,
                cancellationToken);
        }
    }

    private async Task PersistSuccessAsync(FeedbackAnalysisLease lease,
        FeedbackReportResponse report, ProviderAnalysisResult providerResult,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        PollFeedbackAnalysis? analysis = await LockCurrentAsync(lease, cancellationToken);
        if (!OwnsLease(analysis, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        analysis!.ReportJson = JsonSerializer.Serialize(report, JsonOptions);
        analysis.PromptTokens = providerResult.PromptTokens;
        analysis.CompletionTokens = providerResult.CompletionTokens;
        analysis.Status = FeedbackAnalysisStatus.Completed;
        analysis.ErrorCode = null;
        analysis.CompletedAt = timeProvider.GetUtcNow().UtcDateTime;
        analysis.NextAttemptAt = null;
        analysis.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(FeedbackAnalysisLease lease, string code,
        bool retryable, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        PollFeedbackAnalysis? analysis = await LockCurrentAsync(lease, cancellationToken);
        if (!OwnsLease(analysis, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        int maximumAttempts = _options.MaxRetries + 1;
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan minimumRateLimitCooldown = TimeSpan.FromSeconds(
            Math.Max(1, _options.RetryCooldownSeconds));
        if (retryable && analysis!.Attempts < maximumAttempts)
        {
            TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(
                Math.Min(Math.Pow(2, analysis.Attempts) * 5, 3600));
            if ((code is "free_quota_exhausted" or "ai_period_budget_exhausted") &&
                delay < minimumRateLimitCooldown)
                delay = minimumRateLimitCooldown;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            analysis.Status = FeedbackAnalysisStatus.Pending;
            analysis.NextAttemptAt = now + delay;
        }
        else
        {
            analysis!.Status = FeedbackAnalysisStatus.Failed;
            analysis.CompletedAt = now;
            if (code is "free_quota_exhausted" or "ai_period_budget_exhausted")
            {
                TimeSpan cooldown = retryAfter ?? minimumRateLimitCooldown;
                if (cooldown < minimumRateLimitCooldown) cooldown = minimumRateLimitCooldown;
                analysis.NextAttemptAt = now + cooldown;
            }
            else
            {
                analysis.NextAttemptAt = null;
            }
        }
        analysis.ErrorCode = code;
        analysis.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReturnToQueueAsync(FeedbackAnalysisLease lease,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        PollFeedbackAnalysis? analysis = await LockCurrentAsync(lease, cancellationToken);
        if (!OwnsLease(analysis, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        analysis!.Status = FeedbackAnalysisStatus.Pending;
        analysis.NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime;
        analysis.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task<PollFeedbackAnalysis?> LockCurrentAsync(FeedbackAnalysisLease lease,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational()) dbContext.ChangeTracker.Clear();
        return dbContext.Database.IsRelational()
            ? await dbContext.PollFeedbackAnalyses.FromSqlInterpolated($"""
                SELECT * FROM poll_feedback_analyses
                WHERE id = {lease.AnalysisId}
                FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.PollFeedbackAnalyses.SingleOrDefaultAsync(
                item => item.Id == lease.AnalysisId, cancellationToken);
    }

    private static bool OwnsLease(PollFeedbackAnalysis? analysis,
        FeedbackAnalysisLease lease) =>
        analysis is { Status: FeedbackAnalysisStatus.Processing } &&
        analysis.LeaseId == lease.LeaseId && analysis.LeaseGeneration == lease.Generation;

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
