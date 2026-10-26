using System.Data;
using System.Text.Json;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public interface IKnowledgeGradingJobProcessor
{
    Task ProcessAsync(KnowledgeGradingLease lease, CancellationToken cancellationToken);
}

public sealed class KnowledgeGradingJobProcessor(
    ApplicationDbContext dbContext,
    IKnowledgeGradingProvider provider,
    IAiTokenBudgetManager budget,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<KnowledgeGradingJobProcessor> logger) : IKnowledgeGradingJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AiOptions _options = options.Value;

    public async Task ProcessAsync(KnowledgeGradingLease lease,
        CancellationToken cancellationToken)
    {
        PollKnowledgeGradingJob? job = await dbContext.PollKnowledgeGradingJobs
            .SingleOrDefaultAsync(item => item.Id == lease.JobId &&
                item.Status == KnowledgeGradingJobStatus.Processing &&
                item.LeaseId == lease.LeaseId && item.LeaseGeneration == lease.Generation,
                cancellationToken);
        if (job is null) return;
        Guid? reservationId = null;
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
            AiBudgetReservationResult reservation = await budget.ReserveAsync(
                "knowledge_grading", job.Id, job.AutomaticAttempts,
                checked(job.ReservedInputTokens + job.ReservedOutputTokens), cancellationToken);
            if (!reservation.Succeeded)
                throw new FeedbackProviderException("ai_period_budget_exhausted", true,
                    TimeSpan.FromSeconds(reservation.RetryAfterSeconds ?? 60));
            if (reservation.AlreadyExists) return;
            Guid activeReservation = reservation.ReservationId ??
                throw new InvalidOperationException("A successful budget reservation has no ID.");
            reservationId = activeReservation;

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
            catch
            {
                await budget.SettleAsync(activeReservation, null, null, true,
                    CancellationToken.None);
                reservationId = null;
                throw;
            }
            await budget.SettleAsync(activeReservation, providerResult.PromptTokens,
                providerResult.CompletionTokens, false, CancellationToken.None);
            reservationId = null;

            Dictionary<string, Guid> expected = idMap.ToDictionary(item => item.TemporaryId,
                item => item.Snapshot.AnswerId, StringComparer.Ordinal);
            IReadOnlyList<KnowledgeProviderSuggestion> validated = Validate(
                providerResult.Suggestions, expected, job.MaxScoreSnapshot);
            await PersistSuccessAsync(lease, validated, expected, providerResult,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (reservationId is Guid held)
                await budget.SettleAsync(held, null, null, true, CancellationToken.None);
            await ReturnToQueueAsync(lease, CancellationToken.None);
            throw;
        }
        catch (FeedbackProviderException exception)
        {
            if (reservationId is Guid held)
                await budget.SettleAsync(held, null, null, true, CancellationToken.None);
            await RecordFailureAsync(lease, exception.ErrorCode, exception.Retryable,
                exception.RetryAfter, cancellationToken);
        }
        catch (Exception exception)
        {
            if (reservationId is Guid held)
                await budget.SettleAsync(held, null, null, true, CancellationToken.None);
            logger.LogError(exception, "Knowledge grading job {JobId} failed.", lease.JobId);
            await RecordFailureAsync(lease, "knowledge_grading_internal_error", true, null,
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

    private async Task PersistSuccessAsync(KnowledgeGradingLease lease,
        IReadOnlyList<KnowledgeProviderSuggestion> validated,
        IReadOnlyDictionary<string, Guid> expected,
        KnowledgeProviderResult providerResult,
        CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        if (dbContext.Database.IsRelational()) dbContext.ChangeTracker.Clear();
        PollKnowledgeGradingJob? current = dbContext.Database.IsRelational()
            ? await dbContext.PollKnowledgeGradingJobs.FromSqlInterpolated($"""
                SELECT * FROM poll_knowledge_grading_jobs
                WHERE id = {lease.JobId}
                FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.PollKnowledgeGradingJobs.SingleOrDefaultAsync(
                item => item.Id == lease.JobId, cancellationToken);
        if (!OwnsLease(current, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        if (dbContext.Database.IsRelational())
            await dbContext.PollKnowledgeGradingSuggestions
                .Where(item => item.JobId == current!.Id).ExecuteDeleteAsync(cancellationToken);
        else
            dbContext.PollKnowledgeGradingSuggestions.RemoveRange(
                dbContext.PollKnowledgeGradingSuggestions.Where(item => item.JobId == current!.Id));
        foreach (KnowledgeProviderSuggestion item in validated)
        {
            dbContext.PollKnowledgeGradingSuggestions.Add(new()
            {
                Id = Guid.NewGuid(),
                JobId = current!.Id,
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
        current!.SuggestedCount = validated.Count;
        current.PromptTokens = providerResult.PromptTokens;
        current.CompletionTokens = providerResult.CompletionTokens;
        current.Status = KnowledgeGradingJobStatus.Completed;
        current.ErrorCode = null;
        current.CompletedAt = now;
        current.NextAttemptAt = null;
        current.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(KnowledgeGradingLease lease, string code,
        bool retryable, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        PollKnowledgeGradingJob? job = await LockCurrentAsync(lease, cancellationToken);
        if (!OwnsLease(job, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        int maximumAttempts = _options.MaxRetries + 1;
        TimeSpan minimumCooldown = TimeSpan.FromSeconds(Math.Max(1,
            _options.RetryCooldownSeconds));
        if (retryable && job!.AutomaticAttempts < maximumAttempts)
        {
            TimeSpan delay = retryAfter ?? TimeSpan.FromSeconds(
                Math.Min(Math.Pow(2, job.AutomaticAttempts) * 5, 3600));
            if ((code == "free_quota_exhausted" || code == "ai_period_budget_exhausted") &&
                delay < minimumCooldown) delay = minimumCooldown;
            job.Status = KnowledgeGradingJobStatus.Pending;
            job.NextAttemptAt = now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        }
        else
        {
            job!.Status = KnowledgeGradingJobStatus.Failed;
            job.CompletedAt = now;
            job.NextAttemptAt = code is "free_quota_exhausted" or "ai_period_budget_exhausted"
                ? now + (retryAfter > minimumCooldown ? retryAfter.Value : minimumCooldown)
                : now + TimeSpan.FromSeconds(_options.KnowledgeManualRetryCooldownSeconds);
        }
        job.ErrorCode = code;
        job.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReturnToQueueAsync(KnowledgeGradingLease lease,
        CancellationToken cancellationToken)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken) : null;
        PollKnowledgeGradingJob? job = await LockCurrentAsync(lease, cancellationToken);
        if (!OwnsLease(job, lease))
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return;
        }
        job!.Status = KnowledgeGradingJobStatus.Pending;
        job.NextAttemptAt = timeProvider.GetUtcNow().UtcDateTime;
        job.LeaseId = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task<PollKnowledgeGradingJob?> LockCurrentAsync(KnowledgeGradingLease lease,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational()) dbContext.ChangeTracker.Clear();
        return dbContext.Database.IsRelational()
            ? await dbContext.PollKnowledgeGradingJobs.FromSqlInterpolated($"""
                SELECT * FROM poll_knowledge_grading_jobs
                WHERE id = {lease.JobId}
                FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.PollKnowledgeGradingJobs.SingleOrDefaultAsync(
                item => item.Id == lease.JobId, cancellationToken);
    }

    private static bool OwnsLease(PollKnowledgeGradingJob? job, KnowledgeGradingLease lease) =>
        job is { Status: KnowledgeGradingJobStatus.Processing } &&
        job.LeaseId == lease.LeaseId && job.LeaseGeneration == lease.Generation;

    private static FeedbackProviderException Invalid() =>
        new("invalid_provider_response", false);
}
