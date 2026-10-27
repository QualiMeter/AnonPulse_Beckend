using System.Data;
using System.Data.Common;
using AnonPulse.Api.Data;
using AnonPulse.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public interface IFeedbackAnalysisJobQueue
{
    Task<FeedbackAnalysisLease?> ClaimNextAsync(CancellationToken cancellationToken);
}

public sealed record FeedbackAnalysisLease(Guid AnalysisId, Guid LeaseId, int Generation);

public sealed class FeedbackAnalysisJobQueue(
    ApplicationDbContext dbContext,
    IOptions<AiOptions> options,
    TimeProvider timeProvider) : IFeedbackAnalysisJobQueue
{
    private readonly AiOptions _options = options.Value;

    public async Task<FeedbackAnalysisLease?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime staleBefore = now - TimeSpan.FromSeconds(_options.TimeoutSeconds + 30);
        int maximumAttempts = _options.MaxRetries + 1;

        if (!dbContext.Database.IsRelational())
        {
            PollFeedbackAnalysis? item = await dbContext.PollFeedbackAnalyses
                .Where(value => value.Status == FeedbackAnalysisStatus.Pending &&
                                (value.NextAttemptAt == null || value.NextAttemptAt <= now))
                .OrderBy(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (item is null) return null;
            item.Status = FeedbackAnalysisStatus.Processing;
            item.StartedAt = now;
            item.Attempts++;
            Guid leaseId = Guid.NewGuid();
            item.LeaseId = leaseId;
            item.LeaseGeneration++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(item.Id, leaseId, item.LeaseGeneration);
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE poll_feedback_analyses
            SET status = CASE WHEN attempts < {maximumAttempts} THEN 'pending' ELSE 'failed' END,
                next_attempt_at = CASE WHEN attempts < {maximumAttempts} THEN {now} ELSE NULL END,
                completed_at = CASE WHEN attempts < {maximumAttempts} THEN NULL ELSE {now} END,
                error_code = 'worker_interrupted', lease_id = NULL
            WHERE status = 'processing' AND started_at < {staleBefore}
            """, cancellationToken);

        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        Guid relationalLeaseId = Guid.NewGuid();
        command.CommandText = """
            UPDATE poll_feedback_analyses
            SET status = 'processing', started_at = @now, attempts = attempts + 1,
                lease_id = @lease_id, lease_generation = lease_generation + 1
            WHERE id = (
                SELECT id FROM poll_feedback_analyses
                WHERE status = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING id, lease_generation
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "now";
        parameter.Value = now;
        command.Parameters.Add(parameter);
        DbParameter leaseParameter = command.CreateParameter();
        leaseParameter.ParameterName = "lease_id";
        leaseParameter.Value = relationalLeaseId;
        command.Parameters.Add(leaseParameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), relationalLeaseId, reader.GetInt32(1)) : null;
    }
}
