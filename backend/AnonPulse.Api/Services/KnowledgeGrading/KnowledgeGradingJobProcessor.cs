using System.Text.Json;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public interface IKnowledgeGradingJobProcessor
{
    Task ProcessAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class KnowledgeGradingJobProcessor(
    ApplicationDbContext dbContext,
    IKnowledgeGradingProvider provider,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<KnowledgeGradingJobProcessor> logger) : IKnowledgeGradingJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AiOptions _options = options.Value;

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        PollKnowledgeGradingJob? job = await dbContext.PollKnowledgeGradingJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null || job.Status != KnowledgeGradingJobStatus.Processing) return;
        try
        {
            KnowledgeAnswerSnapshot[] snapshot = JsonSerializer.Deserialize<KnowledgeAnswerSnapshot[]>(
                job.AnswersSnapshotJson, JsonOptions) ?? throw Invalid();
            if (snapshot.Length != job.AnswerCount || snapshot.Length == 0 ||
                snapshot.Select(item => item.AnswerId).Distinct().Count() != snapshot.Length ||
                snapshot.Any(item => string.IsNullOrWhiteSpace(item.Text))) throw Invalid();

            var idMap = snapshot.Select((item, index) => new
            {
                Snapshot = item,
                TemporaryId = $"answer-{index + 1:D6}-{Guid.NewGuid():N}"
            }).ToArray();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            KnowledgeProviderResult providerResult;
            try
            {
                providerResult = await provider.GradeAsync(new KnowledgeGradingInput(
                    job.Model, job.ExpectedAnswerSnapshot, job.GradingCriteriaSnapshot,
                    job.MaxScoreSnapshot,
                    idMap.Select(item => new KnowledgeInputAnswer(item.TemporaryId,
                        item.Snapshot.Text)).ToArray(), job.ReservedOutputTokens), timeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FeedbackProviderException("provider_timeout", true, null, exception);
            }

            Dictionary<string, Guid> expected = idMap.ToDictionary(item => item.TemporaryId,
                item => item.Snapshot.AnswerId, StringComparer.Ordinal);
            IReadOnlyList<KnowledgeProviderSuggestion> validated = Validate(
                providerResult.Suggestions, expected, job.MaxScoreSnapshot);
            DateTime now = timeProvider.GetUtcNow().UtcDateTime;
            if (dbContext.Database.IsRelational())
                await dbContext.PollKnowledgeGradingSuggestions
                    .Where(item => item.JobId == job.Id).ExecuteDeleteAsync(cancellationToken);
            else
                dbContext.PollKnowledgeGradingSuggestions.RemoveRange(
                    dbContext.PollKnowledgeGradingSuggestions.Where(item => item.JobId == job.Id));
            foreach (KnowledgeProviderSuggestion item in validated)
            {
                dbContext.PollKnowledgeGradingSuggestions.Add(new()
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    AnswerId = expected[item.TemporaryId],
                    SuggestedScore = item.SuggestedScore,
                    MaxScore = item.MaxScore,
                    Explanation = item.Explanation.Trim(),
                    CorrectPointsJson = JsonSerializer.Serialize(item.CorrectPoints, JsonOptions),
                    MissingOrIncorrectPointsJson = JsonSerializer.Serialize(
                        item.MissingOrIncorrectPoints, JsonOptions),
                    NeedsReview = item.NeedsReview,
                    ReviewReasonsJson = JsonSerializer.Serialize(item.ReviewReasons, JsonOptions),
                    CreatedAt = now
                });
            }
            job.SuggestedCount = validated.Count;
            job.PromptTokens = providerResult.PromptTokens;
            job.CompletionTokens = providerResult.CompletionTokens;
            job.Status = KnowledgeGradingJobStatus.Completed;
            job.ErrorCode = null; job.CompletedAt = now; job.NextAttemptAt = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = KnowledgeGradingJobStatus.Pending;
            job.NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (FeedbackProviderException exception)
        {
            await RecordFailureAsync(job, exception.ErrorCode, exception.Retryable,
                exception.RetryAfter, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Knowledge grading job {JobId} failed.", job.Id);
            await RecordFailureAsync(job, "knowledge_grading_internal_error", true, null,
                cancellationToken);
        }
    }

    internal static IReadOnlyList<KnowledgeProviderSuggestion> Validate(
        IReadOnlyList<KnowledgeProviderSuggestion>? results,
        IReadOnlyDictionary<string, Guid> expected,
        int maxScore)
    {
        if (results is null || results.Count != expected.Count) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (KnowledgeProviderSuggestion item in results)
        {
            if (string.IsNullOrWhiteSpace(item.TemporaryId) ||
                !expected.ContainsKey(item.TemporaryId) || !seen.Add(item.TemporaryId) ||
                item.SuggestedScore < 0 || item.SuggestedScore > maxScore ||
                item.MaxScore != maxScore || string.IsNullOrWhiteSpace(item.Explanation) ||
                item.CorrectPoints is null || item.MissingOrIncorrectPoints is null ||
                item.ReviewReasons is null ||
                item.CorrectPoints.Any(string.IsNullOrWhiteSpace) ||
                item.MissingOrIncorrectPoints.Any(string.IsNullOrWhiteSpace) ||
                item.ReviewReasons.Any(string.IsNullOrWhiteSpace) ||
                (item.NeedsReview && item.ReviewReasons.Count == 0)) throw Invalid();
        }
        return results;
    }

    private async Task RecordFailureAsync(PollKnowledgeGradingJob job, string code,
        bool retryable, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        int maximumAttempts = _options.MaxRetries + 1;
        TimeSpan minimumCooldown = TimeSpan.FromSeconds(Math.Max(1,
            _options.RetryCooldownSeconds));
        if (retryable && job.Attempts < maximumAttempts)
        {
            TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(
                Math.Min(Math.Pow(2, job.Attempts) * 5, 3600));
            if (code == "free_quota_exhausted" && delay < minimumCooldown)
                delay = minimumCooldown;
            job.Status = KnowledgeGradingJobStatus.Pending;
            job.NextAttemptAt = now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
        else
        {
            job.Status = KnowledgeGradingJobStatus.Failed;
            job.CompletedAt = now;
            job.NextAttemptAt = code == "free_quota_exhausted"
                ? now + (retryAfter > minimumCooldown ? retryAfter.Value : minimumCooldown)
                : null;
        }
        job.ErrorCode = code;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FeedbackProviderException Invalid() =>
        new("invalid_provider_response", false);
}
